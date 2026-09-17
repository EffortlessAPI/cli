using System.ComponentModel;
using Effortless.Cli.Project;
using Newtonsoft.Json.Linq;

namespace Effortless.Cli.Tests;

public sealed class ProjectToolFreshnessTests
{
    private const string Head = "v2026.01.01.0002";
    private const string Old = "v2025.12.31.2359";

    // D17's contract: the automatic build-time gate (CommandDispatcher, which
    // always passes clearPins: false) must leave a deliberately pinned step
    // completely alone. Confirmed broken against a real project before this
    // fix: PinnedVersion was silently dropped on every `effortless build`,
    // twice reintroducing a real generator regression the pin existed to
    // avoid — because PinResolves() compared the catalog's always-"v"-prefixed
    // VersionKey against a pin stored WITHOUT one (exactly what Pin()'s own
    // -pin command stores verbatim, and exactly the form the CLI's own -pin
    // help example shows: "effortless mytool -pin 2026.01.01.0000").
    [Fact(DisplayName = "unit-tool-freshness-bare-pin-survives-build: a pin written without a leading v survives the automatic build-time gate")]
    public void BarePinSurvivesAutomaticBuildGate()
    {
        using var directory = new TestDirectory();
        var index = CreateIndex(directory);
        WriteCatalog(index, Catalog());
        var freshness = new ProjectToolFreshness(index);

        var project = new EffortlessProject
        {
            Name = "Demo",
            RootPath = directory.Path,
            ExpandedPaths = [],
            HiddenPaths = [],
            ProjectSettings = new BindingList<ProjectSetting>(),
            ProjectTranspilers = new BindingList<ProjectTranspiler>
            {
                new()
                {
                    Name = "Postgres",
                    RelativePath = "/postgres",
                    // The pin names OLD (not HEAD), and is written bare — no
                    // leading "v" — same as this project's own effortless.json.
                    CommandLine = "effortless/common/to-uppercase -i input.txt",
                    PinnedVersion = Old.TrimStart('v'),
                },
            },
        };

        var plan = freshness.Plan(project, MissingProjectToolPolicy.Fail);
        Assert.True(plan.IsSuccessful, plan.Error);
        var entry = Assert.Single(plan.Entries);
        Assert.True(entry.IsPinned);
        Assert.True(
            entry.HasHonoredPin,
            "a bare (no leading 'v') pin naming a real catalog version must resolve — " +
            "otherwise the automatic build-time gate can never tell a deliberate pin from a stale one.");

        var changed = plan.Apply(project, clearPins: false);

        Assert.False(changed, "the automatic gate must not touch a step whose pin it honors.");
        Assert.Equal(
            Old.TrimStart('v'),
            project.ProjectTranspilers[0].PinnedVersion);
    }

    // The mirror case: -upgrade (clearPins: true) still unpins regardless of
    // the 'v' prefix — this fix only changes whether a pin is HONORED, not
    // whether it can be explicitly cleared.
    [Fact(DisplayName = "unit-tool-freshness-bare-pin-upgrade-clears: -upgrade still clears a bare pin")]
    public void ExplicitUpgradeStillClearsBarePin()
    {
        using var directory = new TestDirectory();
        var index = CreateIndex(directory);
        WriteCatalog(index, Catalog());
        var freshness = new ProjectToolFreshness(index);

        var project = new EffortlessProject
        {
            Name = "Demo",
            RootPath = directory.Path,
            ExpandedPaths = [],
            HiddenPaths = [],
            ProjectSettings = new BindingList<ProjectSetting>(),
            ProjectTranspilers = new BindingList<ProjectTranspiler>
            {
                new()
                {
                    Name = "Postgres",
                    RelativePath = "/postgres",
                    CommandLine = "effortless/common/to-uppercase -i input.txt",
                    PinnedVersion = Old.TrimStart('v'),
                },
            },
        };

        var plan = freshness.Plan(project, MissingProjectToolPolicy.Fail);
        var changed = plan.Apply(project, clearPins: true);

        Assert.True(changed);
        Assert.Null(project.ProjectTranspilers[0].PinnedVersion);
        Assert.Equal(Head, project.ProjectTranspilers[0].LastVersionUsed);
    }

    private static RemoteToolsIndex CreateIndex(TestDirectory directory) =>
        new(
            new DirectoryInfo(directory.Path),
            cliVersion: "test",
            writeLine: _ => { },
            validateHost: _ => { });

    private static void WriteCatalog(RemoteToolsIndex index, JObject root)
    {
        Directory.CreateDirectory(index.RemoteToolsDirectory.FullName);
        File.WriteAllText(index.IndexFile.FullName, root.ToString());
    }

    private static JObject Catalog() =>
        new()
        {
            ["transpilerVersions"] = new JObject
            {
                ["effortless/common/to-uppercase"] = new JObject
                {
                    [Head] = Version("uppercase-head", isHead: true),
                    [Old] = Version("uppercase-old", isHead: false),
                },
            },
            ["cliUpdateAvailable"] = false,
            ["latestBridgeVersion"] = null,
            ["fetchedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };

    private static JObject Version(string path, bool isHead) =>
        new()
        {
            ["metaData"] = new JObject { ["isHeadVersion"] = isHead },
            ["urls"] = new JObject { ["post"] = $"https://tools.example.test/{path}/" },
        };
}
