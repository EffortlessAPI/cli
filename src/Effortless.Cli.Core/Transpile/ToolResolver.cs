using Effortless.Cli.LocalTools;
using Effortless.Cli.Options;
using Effortless.Cli.Project;
using Effortless.Cli.Text;

namespace Effortless.Cli;

/// <summary>
/// Applies the CLI tool-resolution precedence rules to a mutable invocation.
/// Network refresh behavior is delegated to <see cref="RemoteToolsIndex"/>.
/// </summary>
public sealed class ToolResolver
{
    private readonly RemoteToolsIndex _remoteTools;
    private readonly LocalToolResolver _localTools;

    public ToolResolver(
        RemoteToolsIndex remoteTools,
        LocalToolResolver localTools = null)
    {
        _remoteTools = remoteTools
            ?? throw new ArgumentNullException(nameof(remoteTools));
        _localTools = localTools;
    }

    /// <summary>
    /// Populates URL, account, tool, and version metadata on
    /// <paramref name="invocation"/>. An unresolved name deliberately leaves
    /// <see cref="CliInvocation.TargetUrl"/> null.
    /// </summary>
    public void Resolve(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        invocation.Options ??= new CliOptions();
        invocation.IsCatalogResolved = false;
        invocation.IsUserSetToolUrl = false;

        if (string.IsNullOrWhiteSpace(invocation.Account))
        {
            invocation.Account = invocation.Options.account ?? string.Empty;
        }

        // R1: an explicit target URL never consults either catalog.
        var explicitTarget = !string.IsNullOrWhiteSpace(
            invocation.Options.targetUrl)
            ? invocation.Options.targetUrl
            : invocation.TargetUrl;
        if (!string.IsNullOrWhiteSpace(explicitTarget))
        {
            ApplyDirectUrl(invocation, explicitTarget);
            return;
        }

        var rawName = GetRawToolName(invocation);
        invocation.RawTranspilerArg = rawName;
        if (string.IsNullOrWhiteSpace(rawName))
        {
            invocation.TargetUrl = null;
            return;
        }

        // R2: a URL in the tool position behaves exactly like -targetUrl.
        if (IsHttpUrl(rawName))
        {
            ApplyDirectUrl(invocation, rawName);
            return;
        }

        invocation.Transpiler = rawName;

        // Management commands consume the raw name themselves. Internal bridge
        // invocations skip the catalog but may still resolve their managed URL.
        if (IsManagementCommand(invocation.Options))
        {
            invocation.TargetUrl = null;
            return;
        }

        var localOverride = _remoteTools.TryGetToolUrl(rawName);

        // R12: a project-local tool resolves before the catalog is consulted.
        // A tool_urls.json mapping (R4) still wins so it can be redirected.
        if (string.IsNullOrWhiteSpace(localOverride)
            && !invocation.SkipRemoteToolsLookup
            && _localTools?.Find(invocation, rawName) is { } localTool)
        {
            ApplyLocalTool(invocation, localTool);
            return;
        }

        RemoteToolResolution remote = null;
        if (!invocation.SkipRemoteToolsLookup)
        {
            if (string.IsNullOrWhiteSpace(localOverride)
                && !_remoteTools.EnsureFresh())
            {
                invocation.CatalogRefreshFailed = true;
                invocation.TargetUrl = null;
                return;
            }

            // A tool_urls-only tool must not cause a bridge call. Known catalog
            // tools are still resolved so version metadata survives R5.
            if (string.IsNullOrWhiteSpace(localOverride)
                || _remoteTools.ContainsTool(rawName))
            {
                remote = _remoteTools.Resolve(
                    rawName,
                    FindHardPin(invocation, rawName));
                ApplyRemoteMetadata(invocation, remote);
            }

            // D20: never fail on the compound "acct/tool" form alone. When the
            // whole name does not resolve, retry the bare tool and keep the
            // prefix as the account, exactly as "tool -account acct" would.
            if ((remote is null || string.IsNullOrWhiteSpace(remote.Url))
                && string.IsNullOrWhiteSpace(localOverride)
                && TrySplitAccount(rawName, out var account, out var bareTool))
            {
                var bareOverride = _remoteTools.TryGetToolUrl(bareTool);
                if (!string.IsNullOrWhiteSpace(bareOverride))
                {
                    invocation.Account = account;
                    ApplyLocalOverride(
                        invocation,
                        bareTool,
                        bareOverride,
                        remote: null);
                    return;
                }

                var bareRemote = _remoteTools.Resolve(
                    bareTool,
                    FindHardPin(invocation, bareTool));
                if (bareRemote is not null
                    && !string.IsNullOrWhiteSpace(bareRemote.Url))
                {
                    invocation.Account = account;
                    ApplyRemoteMetadata(invocation, bareRemote);
                    invocation.TargetUrl = bareRemote.Url;
                    invocation.Transpiler =
                        NameHelpers.SanitizeUrlForFilename(bareRemote.Url);
                    invocation.IsCatalogResolved = true;
                    return;
                }
            }
        }

        // R4/R5: the raw-name mapping wins even after a catalog match.
        if (!string.IsNullOrWhiteSpace(localOverride))
        {
            ApplyLocalOverride(invocation, rawName, localOverride, remote);
            return;
        }

        if (remote != null && !string.IsNullOrWhiteSpace(remote.Url))
        {
            invocation.TargetUrl = remote.Url;
            invocation.Transpiler =
                NameHelpers.SanitizeUrlForFilename(remote.Url);
            invocation.IsCatalogResolved = true;
            return;
        }

        // R4 also supports the internal bridge run, where the remote lookup is
        // intentionally disabled.
        var mappedUrl = _remoteTools.TryGetToolUrl(rawName);
        if (!string.IsNullOrWhiteSpace(mappedUrl))
        {
            ApplyLocalOverride(invocation, rawName, mappedUrl, remote);
            return;
        }

        // R6: retain an unresolved short name, but split a qualified account.
        invocation.TargetUrl = null;
        if (remote?.HasSpecificError != true)
        {
            ApplyAccountPrefix(invocation, rawName);
        }
    }

    /// <summary>
    /// R11: re-runs catalog resolution for an invocation whose catalog-resolved
    /// tool completely timed out, after the caller forced a refresh. Returns the
    /// new target URL (or null when the tool no longer resolves).
    /// </summary>
    public string ReResolveFromCatalog(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!invocation.IsCatalogResolved)
        {
            return invocation.TargetUrl;
        }

        invocation.TargetUrl = null;
        Resolve(invocation);
        return invocation.TargetUrl;
    }

    private static void ApplyDirectUrl(
        CliInvocation invocation,
        string url)
    {
        invocation.TargetUrl = url;
        invocation.Transpiler = NameHelpers.SanitizeUrlForFilename(url);
        invocation.ResolvedVersionKey = null;
        invocation.ResolvedVersionUrl = null;
        invocation.ResolvedVersionLabel = null;
        invocation.ResolvedToolName = null;
        invocation.HasExplicitVersionError = false;
    }

    private void ApplyLocalTool(
        CliInvocation invocation,
        LocalTool localTool)
    {
        invocation.LocalTool = localTool;
        invocation.LedgerKey = localTool.LedgerKey;
        invocation.Transpiler = localTool.Name;
        invocation.TargetUrl = _localTools.UrlFor(
            localTool,
            invocation.Options.debug);
        invocation.ResolvedVersionKey = null;
        invocation.ResolvedVersionUrl = null;
        invocation.ResolvedVersionLabel = $"{localTool.Name} [local]";
        invocation.ResolvedToolName = null;
        invocation.HasExplicitVersionError = false;
        invocation.IsCatalogResolved = false;
    }

    private static void ApplyRemoteMetadata(
        CliInvocation invocation,
        RemoteToolResolution resolution)
    {
        if (resolution == null)
        {
            return;
        }

        invocation.ResolvedVersionKey = resolution.VersionKey;
        invocation.ResolvedVersionUrl = resolution.Url;
        invocation.ResolvedVersionLabel = resolution.Label;
        invocation.ResolvedToolName = resolution.ToolName;
        invocation.HasExplicitVersionError =
            resolution.HasExplicitVersionError;
    }

    private static void ApplyLocalOverride(
        CliInvocation invocation,
        string rawName,
        string url,
        RemoteToolResolution remote)
    {
        invocation.TargetUrl = url;
        invocation.Transpiler = NameHelpers.SanitizeUrlForFilename(url);
        invocation.ResolvedVersionLabel = $"{rawName} [user-set]";
        invocation.IsUserSetToolUrl = true;
        invocation.HasExplicitVersionError =
            remote?.HasExplicitVersionError == true;

        // A pure local mapping has no remote metadata. R5 intentionally retains
        // metadata that was already resolved for a catalog-backed tool.
        if (remote == null)
        {
            invocation.ResolvedVersionUrl = url;
        }

        ApplyAccountPrefix(invocation, rawName, changeTranspiler: false);
    }

    private static string GetRawToolName(CliInvocation invocation)
    {
        if (!string.IsNullOrWhiteSpace(invocation.RawTranspilerArg))
        {
            return invocation.RawTranspilerArg;
        }

        if (!string.IsNullOrWhiteSpace(invocation.Transpiler))
        {
            return invocation.Transpiler;
        }

        return invocation.RemainingArguments?.FirstOrDefault();
    }

    private static string FindHardPin(
        CliInvocation invocation,
        string rawName)
    {
        var project = invocation.Project;
        if (project?.ProjectTranspilers == null)
        {
            return null;
        }

        var matches = project.ProjectTranspilers
            .Where(projectTranspiler =>
                ToolNameMatches(
                    FirstCommandToken(projectTranspiler.CommandLine),
                    rawName))
            .ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        var selected = MatchCurrentDirectory(
            matches,
            project,
            invocation.CurrentDirectory)
            ?? matches[0];
        return selected.PinnedVersion;
    }

    private static ProjectTranspiler MatchCurrentDirectory(
        IReadOnlyList<ProjectTranspiler> matches,
        EffortlessProject project,
        string currentDirectory)
    {
        if (matches.Count < 2
            || string.IsNullOrWhiteSpace(project.RootPath)
            || string.IsNullOrWhiteSpace(currentDirectory))
        {
            return matches.FirstOrDefault();
        }

        string root;
        string current;
        try
        {
            root = Path.GetFullPath(project.RootPath)
                .Replace('\\', '/')
                .TrimEnd('/');
            current = Path.GetFullPath(currentDirectory)
                .Replace('\\', '/')
                .TrimEnd('/');
        }
        catch
        {
            return matches.FirstOrDefault();
        }

        if (!current.StartsWith(
                root,
                StringComparison.OrdinalIgnoreCase))
        {
            return matches.FirstOrDefault();
        }

        var relative = "/"
            + current.Substring(root.Length).TrimStart('/');
        return matches.FirstOrDefault(projectTranspiler =>
            string.Equals(
                NormalizeRelativePath(projectTranspiler.RelativePath),
                NormalizeRelativePath(relative),
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool ToolNameMatches(
        string commandTool,
        string requestedTool)
    {
        if (string.IsNullOrWhiteSpace(commandTool)
            || string.IsNullOrWhiteSpace(requestedTool))
        {
            return false;
        }

        var commandBase = StripVersionSuffix(commandTool);
        var requestedBase = StripVersionSuffix(requestedTool);
        if (string.Equals(
                commandBase,
                requestedBase,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            ShortName(commandBase),
            ShortName(requestedBase),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyAccountPrefix(
        CliInvocation invocation,
        string rawName,
        bool changeTranspiler = true)
    {
        var toolName = StripVersionSuffix(rawName);
        var slash = toolName.IndexOf('/');
        if (slash <= 0)
        {
            return;
        }

        invocation.Account = toolName.Substring(0, slash);
        if (changeTranspiler)
        {
            invocation.Transpiler = toolName.Substring(slash + 1);
        }
    }

    /// <summary>
    /// D20: splits "acct/tool" into its account prefix and bare tool name.
    /// Returns false for a bare name, a leading slash, or a trailing "/vN"
    /// version suffix, which is a version and not an account.
    /// </summary>
    private static bool TrySplitAccount(
        string rawName,
        out string account,
        out string toolName)
    {
        account = null;
        toolName = null;
        var stripped = StripVersionSuffix(rawName ?? string.Empty);
        var slash = stripped.IndexOf('/');
        if (slash <= 0 || slash == stripped.Length - 1)
        {
            return false;
        }

        account = stripped.Substring(0, slash);
        toolName = stripped.Substring(slash + 1);
        return true;
    }

    private static bool IsManagementCommand(CliOptions options)
    {
        return options.listVersions
            || options.refreshTools
            || options.listTools
            || !string.IsNullOrWhiteSpace(options.searchTools)
            || options.listToolUrls
            || options.version
            || options.init
            || options.uninstall
            || options.login
            || options.projectLogin
            || options.logout
            || options.plan
            || options.info
            || options.help
            || options.upgrade
            || options.checkVersion
            || options.upgradeAll
            || options.listSeeds
            || options.cloneSeed
            || options.listSeedSources
            || !string.IsNullOrWhiteSpace(options.addSeedSource)
            || !string.IsNullOrWhiteSpace(options.removeSeedSource)
            || options.serve
            || !string.IsNullOrWhiteSpace(options.viewToolUrl)
            || !string.IsNullOrWhiteSpace(options.setToolUrl)
            || !string.IsNullOrWhiteSpace(options.removeToolUrl)
            || options.enable
            || options.disable
            || !string.IsNullOrWhiteSpace(options.pin);
    }

    private static bool IsHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp
                || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string FirstCommandToken(string commandLine)
    {
        return commandLine?
            .Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
    }

    private static string StripVersionSuffix(string toolName)
    {
        var slash = toolName.LastIndexOf('/');
        if (slash < 0 || slash == toolName.Length - 1)
        {
            return toolName;
        }

        var last = toolName.Substring(slash + 1);
        return last.Length > 1
            && (last[0] == 'v' || last[0] == 'V')
            && char.IsDigit(last[1])
                ? toolName.Substring(0, slash)
                : toolName;
    }

    private static string ShortName(string toolName)
    {
        var slash = toolName.LastIndexOf('/');
        return slash < 0
            ? toolName
            : toolName.Substring(slash + 1);
    }

    private static string NormalizeRelativePath(string path)
    {
        return "/" + (path ?? string.Empty)
            .Replace('\\', '/')
            .Trim('/');
    }
}
