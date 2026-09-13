using Effortless.Cli.Options;
using Effortless.Cli.Project;

namespace Effortless.Cli.Commands;

public sealed class VersionCommands
{
    private readonly RemoteToolsIndex _index;

    public VersionCommands(RemoteToolsIndex index)
    {
        _index = index;
    }

    public int Refresh(bool debug = false)
    {
        var cachedVersion = _index.CliVersionFile.Exists
            ? File.ReadAllText(_index.CliVersionFile.FullName).Trim()
            : null;
        Delete(_index.IndexFile, debug);
        Delete(_index.CliVersionFile, debug);
        Delete(_index.BridgeVersionIndexFile, debug);
        CliLog.LogLine("Refreshing remote tools index...");
        var succeeded = _index.Refresh(
            "RefreshRemoteTools: explicit -refreshTools invocation",
            $"User invoked -refreshTools; deleted effortless-tools.json, cli_version, and bridge_version_index to force a full re-fetch. cached CLI version was '{cachedVersion ?? "(none)"}', current is '{_index.CliVersion}'.");
        if (!succeeded)
        {
            WriteError(
                $"ERROR: Remote tools index refresh failed: {_index.LastRefreshError}");
            return -1;
        }

        CliLog.LogLine("Remote tools index refreshed.");
        return 0;
    }

    public int ListVersions(string toolName)
    {
        var versions = _index.ListVersions(toolName);
        if (versions is null)
        {
            Console.WriteLine(
                $"No versions for '{toolName}' were found in the remote tools index.");
            return 0;
        }

        Console.WriteLine(
            $"Available versions for {versions.ToolName}:");
        Console.WriteLine();
        foreach (var version in versions.Versions)
        {
            Console.WriteLine(
                $"  {version.VersionKey}{(version.IsHead ? " (latest)" : string.Empty)}");
            Console.WriteLine($"    url: {version.Url}");
        }

        var overrideUrl =
            Config.ToolUrls.TryGetUrlFromFileUrls(toolName);
        if (!string.IsNullOrEmpty(overrideUrl))
        {
            Console.WriteLine();
            Console.WriteLine(
                $"  * globally overridden via effortless -setToolUrl {toolName}={overrideUrl}");
            Console.WriteLine(
                $"    run 'effortless -removeToolUrl {toolName}' to reset");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Run with: effortless {toolName}/<versionKey>");
        Console.WriteLine($"Run latest: effortless {toolName}");
        return 0;
    }

    public int Upgrade(CliInvocation invocation, bool all)
    {
        var refreshResult = Refresh(invocation.Options.debug);
        if (refreshResult != 0)
        {
            return refreshResult;
        }

        var project = invocation.Project
                      ?? ProjectLocator.TryToLoad(
                          new DirectoryInfo(
                              invocation.CurrentDirectory));
        if (project is null)
        {
            WriteError(
                "No effortless.json project found in this directory.");
            return 0;
        }

        var toolName = invocation.RawTranspilerArg
                       ?? invocation.Transpiler;
        if (all || string.IsNullOrEmpty(toolName))
        {
            return UpgradeAll(project);
        }

        var head = _index.Resolve(toolName);
        if (head is null || string.IsNullOrEmpty(head.Url))
        {
            WriteError(
                $"Could not resolve tool '{toolName}' from the remote tools index.");
            return 0;
        }

        var matches = project.ProjectTranspilers
            .Where(step => EffortlessProject.ToolNameMatches(
                EffortlessProject.GetToolName(step.CommandLine),
                toolName))
            .ToList();
        var matched = MatchCurrentDirectory(
            matches,
            project,
            invocation.CurrentDirectory);
        var overrideUrl = _index.TryGetToolUrl(toolName);
        if (matched is null)
        {
            Console.WriteLine(
                $"{toolName} is not used in this project — nothing to unpin here.");
            if (string.IsNullOrEmpty(overrideUrl))
            {
                Console.WriteLine(
                    $"Refreshed the core tools index; '{toolName}' will track latest (HEAD {head.VersionKey}) wherever it is used unpinned.");
            }
            else
            {
                WriteOverrideWarning(toolName, overrideUrl, head.VersionKey);
            }

            return 0;
        }

        var oldVersion = matched.PinnedVersion
                         ?? matched.LastVersionUsed
                         ?? "(unpinned)";
        matched.PinnedVersion = null;
        matched.LastVersionUsed = head.VersionKey;
        project.Save();
        Console.WriteLine(
            $"Upgraded {toolName}: {oldVersion} → HEAD ({head.VersionKey}, unpinned — will track latest)");
        if (!string.IsNullOrEmpty(overrideUrl))
        {
            WriteOverrideWarning(toolName, overrideUrl, head.VersionKey);
        }

        return 0;
    }

    private static void WriteOverrideWarning(
        string toolName,
        string overrideUrl,
        string headVersion) =>
        WriteError(
            $"WARNING: '{toolName}' will NOT run HEAD ({headVersion}): ~/.effortless/tool_urls.json overrides it with {overrideUrl}. Run 'effortless -removeToolUrl {toolName}' to use HEAD.");

    /// <summary>
    /// D17: pins this project's step to a catalog version key or a literal
    /// URL. Both are stored the same way in <c>PinnedVersion</c>; the CLI does
    /// not distinguish at pin time. <c>-upgrade</c> (alias unpin) clears it.
    /// </summary>
    public int Pin(CliInvocation invocation, string pinnedValue)
    {
        var project = invocation.Project
                      ?? ProjectLocator.TryToLoad(
                          new DirectoryInfo(
                              invocation.CurrentDirectory));
        if (project is null)
        {
            WriteError(
                "No effortless.json project found in this directory.");
            return -1;
        }

        var toolName = invocation.RawTranspilerArg
                       ?? invocation.Transpiler;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            WriteError(
                "Please specify a transpiler name to pin.");
            return -1;
        }

        var matches = project.ProjectTranspilers
            .Where(step => EffortlessProject.ToolNameMatches(
                EffortlessProject.GetToolName(step.CommandLine),
                toolName))
            .ToList();
        var matched = MatchCurrentDirectory(
            matches,
            project,
            invocation.CurrentDirectory);
        if (matched is null)
        {
            WriteError(
                $"{toolName} is not installed in this project — nothing to pin.");
            return -1;
        }

        matched.PinnedVersion = pinnedValue;
        project.Save();
        Console.WriteLine(
            $"Pinned {toolName} to {pinnedValue}");
        Console.WriteLine(
            $"Run 'effortless upgrade {toolName}' to remove the pin and track HEAD again.");
        return 0;
    }

    public int ListTools(string search = null) =>
        ListTools(new CatalogQuery(search), json: false);

    /// <summary>
    /// listTools / searchTools (step 11): filters the cached catalog and prints an
    /// aligned table, or the entries as JSON with -json.
    /// </summary>
    public int ListTools(CatalogQuery query, bool json)
    {
        query ??= new CatalogQuery();
        var search = query.Text;
        var tools = _index.ListTools(query);

        if (json)
        {
            Console.WriteLine(
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    tools.Select(tool => new
                    {
                        canonicalName = tool.CanonicalName,
                        shortName = tool.ShortName,
                        account = tool.Account,
                        category = tool.Category,
                        headVersion = tool.HeadVersion,
                        headCreatedAt = tool.HeadCreatedAt?.UtcDateTime.ToString(
                            "yyyy-MM-ddTHH:mm:ssZ",
                            System.Globalization.CultureInfo.InvariantCulture),
                        versionCount = tool.VersionCount,
                        requiresApiKey = tool.RequiresApiKey,
                        monthlyRequestCount = tool.MonthlyRequestCount,
                        description = tool.Description,
                        tags = tool.Tags,
                    }),
                    Newtonsoft.Json.Formatting.Indented));
            return 0;
        }

        if (!string.IsNullOrEmpty(search) && tools.Count == 0)
        {
            Console.WriteLine($"No tools matched '{search}'.");
            return 0;
        }

        Console.WriteLine(
            string.IsNullOrEmpty(search)
                ? $"Available tools ({tools.Count}):"
                : $"Tools matching '{search}' ({tools.Count}):");
        Console.WriteLine();
        if (tools.Count == 0)
        {
            return 0;
        }

        var nameWidth = Math.Max("NAME".Length, tools.Max(tool => tool.CanonicalName.Length));
        var headWidth = Math.Max("HEAD".Length, tools.Max(tool => (tool.HeadVersion ?? "NO HEAD").Length));
        Console.WriteLine(
            $"  {"NAME".PadRight(nameWidth)}  {"HEAD".PadRight(headWidth)}  UPDATED     VERSIONS  KEY  DESCRIPTION");
        foreach (var tool in tools)
        {
            var updated = tool.HeadCreatedAt?.UtcDateTime.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture) ?? "-";
            var versions = tool.VersionCount > 0
                ? tool.VersionCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "-";
            var key = tool.RequiresApiKey switch
            {
                true => "yes",
                false => "no",
                _ => "-",
            };
            Console.WriteLine(
                $"  {tool.CanonicalName.PadRight(nameWidth)}  {(tool.HeadVersion ?? "NO HEAD").PadRight(headWidth)}  {updated,-10}  {versions,8}  {key,-3}  {Truncate(tool.Description, 60)}");
        }

        return 0;
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var single = string.Join(
            " ",
            value.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return single.Length <= max ? single : single[..(max - 1)].TrimEnd() + "\u2026";
    }

    private int UpgradeAll(EffortlessProject project)
    {
        if (project.ProjectTranspilers.Count == 0)
        {
            Console.WriteLine(
                "No transpilers found in effortless.json.");
            return 0;
        }

        var plan = new ProjectToolFreshness(_index).Plan(
            project,
            MissingProjectToolPolicy.Skip);
        var changedCount = plan.ChangedCount;
        var missingCount = plan.MissingCount;
        var overriddenCount = 0;
        foreach (var entry in plan.Entries)
        {
            if (entry.IsMissing)
            {
                Console.WriteLine(
                    $"  SKIP {entry.ToolName} — not found in remote tools index");
                continue;
            }

            if (!entry.NeedsChange)
            {
                Console.WriteLine(
                    $"  OK   {entry.ToolName} — already unpinned at HEAD ({entry.HeadVersion})");
            }
            else
            {
                Console.WriteLine(
                    $"  UP   {entry.ToolName}: {entry.PreviousVersion} → HEAD ({entry.HeadVersion}, unpinned)");
            }

            var overrideUrl = _index.TryGetToolUrl(entry.ToolName);
            if (!string.IsNullOrEmpty(overrideUrl))
            {
                overriddenCount++;
                WriteError(
                    $"  OVERRIDE {entry.ToolName} — runs {overrideUrl} from ~/.effortless/tool_urls.json, NOT HEAD; run 'effortless -removeToolUrl {entry.ToolName}' to use HEAD");
            }
        }

        plan.Apply(project, clearPins: true);
        Console.WriteLine(
            $"\nUpgraded {changedCount} tool(s)"
            + (missingCount > 0
                ? $", skipped {missingCount}"
                : string.Empty)
            + (overriddenCount > 0
                ? $", {overriddenCount} overridden by tool_urls.json"
                : string.Empty)
            + ".");
        return 0;
    }

    private static ProjectTranspiler MatchCurrentDirectory(
        IReadOnlyList<ProjectTranspiler> matches,
        EffortlessProject project,
        string currentDirectory)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        var relative = project.GetProjectRelativePath(
            currentDirectory);
        return matches.FirstOrDefault(
                   step => string.Equals(
                       step.RelativePath,
                       relative,
                       StringComparison.OrdinalIgnoreCase))
               ?? matches[0];
    }

    private static void Delete(FileInfo file, bool debug)
    {
        file.Refresh();
        if (!file.Exists)
        {
            return;
        }

        file.Delete();
        if (debug)
        {
            Console.WriteLine($"DEBUG: deleted {file.FullName}");
        }
    }

    private static void WriteError(string message)
    {
        var color = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ForegroundColor = color;
    }
}
