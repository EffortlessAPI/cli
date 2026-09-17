namespace Effortless.Cli.Project;

public enum MissingProjectToolPolicy
{
    Fail,
    Skip,
}

/// <summary>
/// Resolves project tools to catalog HEAD before applying any version changes.
/// </summary>
public sealed class ProjectToolFreshness
{
    private readonly RemoteToolsIndex _index;

    public ProjectToolFreshness(RemoteToolsIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public ProjectToolUpgradePlan Plan(
        EffortlessProject project,
        MissingProjectToolPolicy missingToolPolicy)
    {
        ArgumentNullException.ThrowIfNull(project);

        var entries = new List<ProjectToolUpgradeEntry>();
        var localTools = string.IsNullOrWhiteSpace(project.RootPath)
            ? null
            : LocalTools.LocalToolCatalog.Discover(project.RootPath);
        foreach (var step in project.ProjectTranspilers
                     ?? Enumerable.Empty<ProjectTranspiler>())
        {
            var tool = EffortlessProject.GetToolName(step.CommandLine);
            if (IsExcluded(tool))
            {
                continue;
            }

            // R12: a project-local tool is not catalog-versioned.
            if (localTools?.Match(tool) is not null)
            {
                continue;
            }

            var head = _index.ResolveHead(tool);
            if (head is null
                || head.HasSpecificError
                || string.IsNullOrWhiteSpace(head.Url)
                || string.IsNullOrWhiteSpace(head.VersionKey))
            {
                if (HasCustomUrl(tool))
                {
                    continue;
                }

                entries.Add(
                    ProjectToolUpgradeEntry.Missing(step, tool));
                if (missingToolPolicy == MissingProjectToolPolicy.Fail)
                {
                    return ProjectToolUpgradePlan.Failed(
                        entries,
                        $"Project tool '{tool}' is missing from the current remote tools index or has no HEAD version.");
                }

                continue;
            }

            entries.Add(
                ProjectToolUpgradeEntry.Resolved(
                    step,
                    tool,
                    head,
                    pinResolves: PinResolves(tool, step.PinnedVersion)));
        }

        return ProjectToolUpgradePlan.Succeeded(entries);
    }

    /// <summary>
    /// D17: a pin is honored by the automatic gate only while it still names
    /// something the catalog can resolve. A pin that has fallen out of the
    /// catalog is stale, not deliberate, and step-03A's currentness gate still
    /// clears it so the build does not break silently.
    /// </summary>
    private bool PinResolves(string tool, string pinnedVersion)
    {
        if (string.IsNullOrWhiteSpace(pinnedVersion))
        {
            return false;
        }

        // A pin may be a literal URL rather than a catalog version key.
        if (Uri.TryCreate(pinnedVersion, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            return true;
        }

        var versions = _index.ListVersions(tool);
        return versions is not null
            && versions.Versions.Any(version => VersionKeysMatch(version.VersionKey, pinnedVersion));
    }

    // Catalog version keys are always "v"-prefixed (e.g. "v2026.09.13.2037"),
    // but a pin is commonly written without one — the CLI's own `-pin` help
    // example is bare ("effortless mytool -pin 2026.01.01.0000"), Pin() stores
    // whatever the user typed with no normalization, and this repo's
    // effortless.json has stored pins that way. An ordinal-only comparison
    // here never matched a bare pin against the prefixed catalog key, so
    // HasHonoredPin was always false and D17's clearPins:false guard (the
    // automatic build-time gate) never engaged — a deliberately pinned step
    // silently lost its pin on every single build. Confirmed against a real
    // project: rulebook-to-postgres's pin was wiped twice in one session, each
    // time reintroducing a genuine generator regression the pin existed to
    // avoid. Tolerate an optional leading 'v'/'V' on either side; everything
    // else still compares ordinally, so this does not loosen matching in any
    // other way.
    private static bool VersionKeysMatch(string catalogKey, string pinnedVersion)
    {
        static string StripV(string s) =>
            !string.IsNullOrEmpty(s) && (s[0] == 'v' || s[0] == 'V') ? s[1..] : s;
        return string.Equals(StripV(catalogKey), StripV(pinnedVersion), StringComparison.Ordinal);
    }

    private bool HasCustomUrl(string tool)
    {
        var direct = _index.TryGetToolUrl(tool);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return true;
        }

        var slash = tool.LastIndexOf('/');
        return slash >= 0
            && !string.IsNullOrWhiteSpace(
                _index.TryGetToolUrl(tool[(slash + 1)..]));
    }

    private static bool IsExcluded(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool)
            || tool.Equals("-execute", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("-exec", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(tool, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https";
    }
}

public sealed class ProjectToolUpgradePlan
{
    private ProjectToolUpgradePlan(
        bool succeeded,
        IReadOnlyList<ProjectToolUpgradeEntry> entries,
        string error)
    {
        IsSuccessful = succeeded;
        Entries = entries;
        Error = error;
    }

    public bool IsSuccessful { get; }

    public IReadOnlyList<ProjectToolUpgradeEntry> Entries { get; }

    public string Error { get; }

    public int ChangedCount =>
        Entries.Count(entry => entry.NeedsChange);

    public int MissingCount =>
        Entries.Count(entry => entry.IsMissing);

    public int PinnedCount =>
        Entries.Count(entry => entry.IsPinned);

    /// <summary>
    /// Advances every step that needs it to catalog HEAD. D17: with
    /// <paramref name="clearPins"/> false — the automatic build-time gate — a
    /// step carrying a deliberate <c>PinnedVersion</c> is left completely
    /// alone, so <c>-pin</c> survives a build. The explicit upgrade verbs pass
    /// true and unpin.
    /// </summary>
    public bool Apply(
        EffortlessProject project,
        bool clearPins = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!IsSuccessful)
        {
            return false;
        }

        var changed = false;
        foreach (var entry in Entries.Where(entry => entry.NeedsChange))
        {
            if (entry.HasHonoredPin && !clearPins)
            {
                continue;
            }

            entry.Step.ClearCommandLineVersion();
            entry.Step.PinnedVersion = null;
            entry.Step.LastVersionUsed = entry.HeadVersion;
            changed = true;
        }

        if (changed)
        {
            project.Save();
        }

        return changed;
    }

    internal static ProjectToolUpgradePlan Succeeded(
        IReadOnlyList<ProjectToolUpgradeEntry> entries) =>
        new(true, entries, null);

    internal static ProjectToolUpgradePlan Failed(
        IReadOnlyList<ProjectToolUpgradeEntry> entries,
        string error) =>
        new(false, entries, error);
}

public sealed class ProjectToolUpgradeEntry
{
    private ProjectToolUpgradeEntry(
        ProjectTranspiler step,
        string toolName,
        string headVersion,
        bool isMissing,
        bool pinResolves = false)
    {
        Step = step;
        ToolName = toolName;
        HeadVersion = headVersion;
        IsMissing = isMissing;
        PinResolves = pinResolves;
    }

    public ProjectTranspiler Step { get; }

    public string ToolName { get; }

    public string HeadVersion { get; }

    public bool IsMissing { get; }

    /// <summary>
    /// D17: a deliberate <c>-pin</c> is respected by the automatic build-time
    /// gate; only an explicit <c>-upgrade</c>/<c>-upgradeAll</c> clears it.
    /// </summary>
    public bool IsPinned =>
        !string.IsNullOrWhiteSpace(Step.PinnedVersion);

    /// <summary>
    /// True when this step carries a pin the catalog can still satisfy, so the
    /// automatic gate must leave it alone (D17). A pin that no longer resolves
    /// is stale and is still cleared by the gate.
    /// </summary>
    public bool HasHonoredPin => IsPinned && PinResolves;

    public bool PinResolves { get; }

    public bool NeedsChange =>
        !IsMissing
        && (Step.HasCommandLineVersion()
            || IsPinned
            || !string.Equals(
                Step.LastVersionUsed,
                HeadVersion,
                StringComparison.Ordinal));

    public string PreviousVersion =>
        Step.PinnedVersion
        ?? Step.LastVersionUsed
        ?? "(unpinned)";

    internal static ProjectToolUpgradeEntry Missing(
        ProjectTranspiler step,
        string toolName) =>
        new(step, toolName, null, true);

    internal static ProjectToolUpgradeEntry Resolved(
        ProjectTranspiler step,
        string toolName,
        RemoteToolResolution head,
        bool pinResolves = false) =>
        new(step, toolName, head.VersionKey, false, pinResolves);
}
