using Newtonsoft.Json.Linq;
using System.Text.Json.Nodes;
using Effortless.Cli.E2E.Harness;

namespace Effortless.Cli.E2E.Suites;

public sealed class ProjectTests
{
    private readonly CliUnderTest _cli = new();

    [Fact(DisplayName = "proj-init-empty: init scaffolds a project")]
    public async Task InitScaffoldsAProject()
    {
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server, "init-compile-rulebook");
        server.IndexJson = index.Json;
        server.Enqueue("compile-rulebook", ToolBehavior.Files());
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedHome(index);
        var projectPath = CreateDirectory(sandbox.ProjectPath, "demo");

        var result = await _cli.Run(["-init"], projectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Added Setting: project-name: 'demo'", result.Stdout);
        var project = ReadObject(Path.Combine(projectPath, "effortless.json"));
        Assert.Equal("demo", String(project, "Name"));
        Assert.Single(Array(project, "ProjectSettings"));
        Assert.Equal("demo", SettingValue(project, "project-name"));
        var step = Assert.IsType<JsonObject>(Assert.Single(Array(project, "ProjectTranspilers")));
        Assert.Equal("compile-rulebook", String(step, "Name"));
        Assert.Equal("/effortless-rulebook", String(step, "RelativePath"));
        Assert.Equal("compile-rulebook -i effortless-rulebook.json", String(step, "CommandLine"));
        Assert.False(step["IsDisabled"]?.GetValue<bool>());
        Assert.True(Guid.TryParse(String(project, "SSoTmeProjectId"), out _));

        Assert.Equal(StandardGitIgnore, File.ReadAllText(Path.Combine(projectPath, ".gitignore")));
        var environmentTemplate = File.ReadAllText(Path.Combine(projectPath, "effortless.env"));
        Assert.Contains("# AIRTABLE_PAT=xyz", environmentTemplate);
        Assert.Contains("effortless airtable-to-rulebook -account airtable", environmentTemplate);

        // The starter rulebook is a working one, not an empty stub: a build
        // against it produces real output before any rules are written.
        var rulebook = ReadObject(
            Path.Combine(projectPath, "effortless-rulebook", "effortless-rulebook.json"));
        Assert.Equal("demo", rulebook["Name"]?.GetValue<string>());
        var hello = rulebook["HelloWhos"]!.AsObject();
        var schema = hello["schema"]!.AsArray();
        Assert.Equal("HelloWhoId", schema[0]!["name"]!.GetValue<string>());
        Assert.Equal("raw", schema[0]!["type"]!.GetValue<string>());
        Assert.Equal("Name", schema[1]!["name"]!.GetValue<string>());
        Assert.Equal("raw", schema[1]!["type"]!.GetValue<string>());
        Assert.Equal("Introduction", schema[2]!["name"]!.GetValue<string>());
        Assert.Equal("calculated", schema[2]!["type"]!.GetValue<string>());
        Assert.Equal(
            "=\"Hello \" & {{Name}} & \"!\"",
            schema[2]!["formula"]!.GetValue<string>());
        Assert.Equal(3, hello["data"]!.AsArray().Count);

        // The starter rulebook is deliberately ONE entity: a fact you type and
        // a value derived from it. A second entity with a relationship and a
        // cross-table lookup is demo content, not a starting point.
        Assert.Equal(
            new[] { "Name", "Description", "HelloWhos" },
            rulebook.Select(pair => pair.Key).ToArray());
    }

    [Fact(DisplayName = "proj-init-name: init -name overrides the name")]
    public async Task InitNameOverridesTheDirectoryName()
    {
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server, "init-compile-rulebook");
        server.IndexJson = index.Json;
        server.Enqueue("compile-rulebook", ToolBehavior.Files());
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedHome(index);
        var projectPath = CreateDirectory(sandbox.ProjectPath, "not-foo");

        var result = await _cli.Run(["-init", "-name", "Foo"], projectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        var project = ReadObject(Path.Combine(projectPath, "effortless.json"));
        Assert.Equal("Foo", String(project, "Name"));
        Assert.Equal("Foo", SettingValue(project, "project-name"));
    }

    [Fact(DisplayName = "proj-init-bareword: bareword init scaffolds the same project")]
    public async Task BarewordInitScaffoldsAProject()
    {
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server, "init-compile-rulebook");
        server.IndexJson = index.Json;
        server.Enqueue("compile-rulebook", ToolBehavior.Files());
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedHome(index);
        var projectPath = CreateDirectory(sandbox.ProjectPath, "bareword-demo");

        var result = await _cli.Run(["init"], projectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        var project = ReadObject(Path.Combine(projectPath, "effortless.json"));
        Assert.Equal("bareword-demo", String(project, "Name"));
        Assert.Equal("bareword-demo", SettingValue(project, "project-name"));
        var step = Assert.IsType<JsonObject>(Assert.Single(Array(project, "ProjectTranspilers")));
        Assert.Equal("compile-rulebook", String(step, "Name"));
        Assert.True(File.Exists(Path.Combine(projectPath, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(projectPath, "effortless.env")));
        Assert.True(
            File.Exists(
                Path.Combine(projectPath, "effortless-rulebook", "effortless-rulebook.json")));
    }

    [Fact(DisplayName = "proj-init-twice: legacy repeated init exits zero and canonicalizes JSON")]
    public async Task InitTwicePinsLegacySilentZeroExitAndJsonCanonicalization()
    {
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server, "init-compile-rulebook");
        server.IndexJson = index.Json;
        server.Enqueue("compile-rulebook", ToolBehavior.Files(), ToolBehavior.Files());
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedHome(index);
        var projectPath = CreateDirectory(sandbox.ProjectPath, "twice");
        var first = await _cli.Run(["-init"], projectPath, sandbox);
        Assert.Equal(0, first.ExitCode);
        var projectFile = Path.Combine(projectPath, "effortless.json");
        var before = ReadObject(projectFile);

        var second = await _cli.Run(["-init"], projectPath, sandbox);

        // The legacy top-level exception handler swallows this failure and exits zero;
        // loading also serializes root CurrentPath from null to an empty string.
        // Every init implies a build (D-init-implies-build), and the registered
        // compile-rulebook step now has something real to run on every repeat.
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("compile-rulebook", second.Combined);
        var after = ReadObject(projectFile);
        Assert.True(before.ContainsKey("CurrentPath"));
        Assert.Null(before["CurrentPath"]);
        Assert.Equal(string.Empty, after["CurrentPath"]?.GetValue<string>());
        var beforeProjectName = FindSetting(before, "project-name")!;
        var afterProjectName = FindSetting(after, "project-name")!;
        // The implied build after init (D-init-implies-build) re-saves the
        // project once compile-rulebook resolves a version, so the volatile
        // ProjectSettingId is already canonicalized away on the first init,
        // not just the second.
        Assert.False(beforeProjectName.ContainsKey("ProjectSettingId"));
        Assert.False(afterProjectName.ContainsKey("ProjectSettingId"));
        before.Remove("CurrentPath");
        after.Remove("CurrentPath");
        Assert.True(
            JsonNode.DeepEquals(before, after),
            $"Unexpected repeated-init rewrite.{Environment.NewLine}Before: {before}{Environment.NewLine}After: {after}");
    }

    [Fact(DisplayName = "proj-init-rename: legacy init -name truncates a spaced name")]
    public async Task InitNamePinsLegacyTruncationOfSpacedName()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-basic");

        var result = await _cli.Run(
            ["-init", "-name", "New Name"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        var project = Assert.IsType<JsonObject>(sandbox.ProjectFile);
        Assert.Equal("New", String(project, "Name"));
        Assert.Equal("New", SettingValue(project, "project-name"));
    }

    [Fact(DisplayName = "proj-init-implies-build: init on a prepared project runs its registered step")]
    public async Task InitOnAPreparedProjectRunsItsRegisteredStep()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep("Step", "", "to-uppercase"));
        server.Enqueue("to-uppercase", ToolBehavior.Files());

        var result = await cli.Run(["-init"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "proj-init-force-subproject: native argv force creates a nested project")]
    public async Task InitForceCreatesNestedProjectWithoutChangingParent()
    {
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server, "init-compile-rulebook");
        server.IndexJson = index.Json;
        server.Enqueue("compile-rulebook", ToolBehavior.Files());
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedHome(index);
        sandbox.SeedProject("project-basic");
        var parentPath = Path.Combine(sandbox.ProjectPath, "effortless.json");
        var parentBefore = File.ReadAllBytes(parentPath);
        var subPath = CreateDirectory(sandbox.ProjectPath, "sub");

        var result = await _cli.Run(["-init", "force"], subPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(parentBefore, File.ReadAllBytes(parentPath));
        var child = ReadObject(Path.Combine(subPath, "effortless.json"));
        Assert.Equal("sub", String(child, "Name"));
        Assert.Equal("sub", SettingValue(child, "project-name"));
    }

    [Fact(DisplayName = "proj-init-gitignore-append: init appends effortless.env")]
    public async Task InitAppendsEnvironmentFileToExistingGitIgnore()
    {
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server, "init-compile-rulebook");
        server.IndexJson = index.Json;
        server.Enqueue("compile-rulebook", ToolBehavior.Files());
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedHome(index);
        var projectPath = CreateDirectory(sandbox.ProjectPath, "gitignore-demo");
        File.WriteAllText(Path.Combine(projectPath, ".gitignore"), "existing-rule");

        var result = await _cli.Run(["-init"], projectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            $"existing-rule{Environment.NewLine}effortless.env{Environment.NewLine}",
            File.ReadAllText(Path.Combine(projectPath, ".gitignore")));
    }

    [Fact(DisplayName = "proj-walk-up: project commands walk up from nested directories")]
    public async Task ProjectCommandsWalkUpFromNestedDirectories()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-basic");
        var nestedPath = CreateDirectory(sandbox.ProjectPath, "a", "b");

        var result = await _cli.Run(["-describe"], nestedPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"======    {CliReportedPath(sandbox.ProjectPath)}", result.Stdout);
        Assert.Contains("======  Basic Project", result.Stdout);
    }

    [Fact(DisplayName = "proj-migrate-ssotme-json: legacy ssotme files are renamed")]
    public async Task SsotmeProjectAndEnvironmentFilesAreRenamed()
    {
        using var sandbox = Sandbox.Create(_cli);
        var projectJson = MinimalProjectJson("Migrated Project");
        const string environment = "LEGACY_KEY=value\n";
        sandbox.WriteFile("ssotme.json", projectJson);
        sandbox.WriteFile("ssotme.env", environment);

        var result = await _cli.Run(["-describe"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "ssotme.json")));
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "ssotme.env")));
        // The rename keeps the content and adds the one-time migration flag.
        var migrated = JObject.Parse(sandbox.ReadFile("effortless.json"));
        Assert.True(migrated.Value<bool>("MigratedFromSsotme"));
        migrated.Remove("MigratedFromSsotme");
        Assert.True(JToken.DeepEquals(JObject.Parse(projectJson), migrated));
        Assert.Equal(environment, sandbox.ReadFile("effortless.env"));
    }

    [Fact(DisplayName = "proj-invalid-candidate: an existing effortless.json blocks the ssotme.json rename")]
    public async Task ExistingEffortlessJsonBlocksSsotmeJsonRename()
    {
        using var sandbox = Sandbox.Create(_cli);
        var projectJson = MinimalProjectJson("Valid Ssotme Project");
        sandbox.WriteFile("effortless.json", "{}");
        sandbox.WriteFile("ssotme.json", projectJson);

        var result = await _cli.Run(["-describe"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(sandbox.ProjectPath, "ssotme.json")));
        Assert.Equal("{}", sandbox.ReadFile("effortless.json"));
        Assert.Contains("======  Valid Ssotme Project", result.Stdout);
    }

    [Fact(DisplayName = "proj-legacy-names: aicapture.json and SSoTmeProject.json still load")]
    public async Task OlderProjectFileNamesLoadAndSaveInPlace()
    {
        foreach (var fileName in new[] { "aicapture.json", "SSoTmeProject.json" })
        {
            using var sandbox = Sandbox.Create(_cli);
            sandbox.WriteFile(fileName, MinimalProjectJson($"Legacy {fileName}"));

            var describe = await _cli.Run(["-describe"], sandbox.ProjectPath, sandbox);
            var save = await _cli.Run(
                ["-addSetting", "legacy=yes"],
                sandbox.ProjectPath,
                sandbox);

            Assert.Equal(0, describe.ExitCode);
            Assert.Equal(0, save.ExitCode);
            Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "effortless.json")));
            Assert.Equal("yes", SettingValue(ReadObject(Path.Combine(sandbox.ProjectPath, fileName)), "legacy"));
        }
    }

    [Fact(DisplayName = "proj-generate-id: loading generates and saves a missing project id")]
    public async Task LoadingGeneratesAndSavesMissingProjectId()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-no-id");
        var before = Assert.IsType<JsonObject>(sandbox.ProjectFile);
        Assert.False(before.ContainsKey("SSoTmeProjectId"));

        var result = await _cli.Run(["-describe"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        var after = Assert.IsType<JsonObject>(sandbox.ProjectFile);
        Assert.True(Guid.TryParse(String(after, "SSoTmeProjectId"), out _));
        Assert.Equal("No Id Project", String(after, "Name"));
        Assert.Equal("kept", SettingValue(after, "custom"));
        Assert.Equal("No Id Project", SettingValue(after, "project-name"));
        Assert.Empty(Array(after, "ProjectTranspilers"));
        Assert.False(after["ShowHidden"]?.GetValue<bool>());
        Assert.False(after["ShowAllFiles"]?.GetValue<bool>());
        Assert.True(after.ContainsKey("CurrentPath"));
        Assert.Null(after["CurrentPath"]);
        Assert.True(after.ContainsKey("SSoTmeProjectFiles"));
        Assert.Null(after["SSoTmeProjectFiles"]);
    }

    [Fact(DisplayName = "proj-describe: describe and list print the project summary")]
    public async Task DescribeAndListPrintProjectSummary()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-describe");

        var describe = await _cli.Run(["-describe"], sandbox.ProjectPath, sandbox);
        var list = await _cli.Run(["list"], sandbox.ProjectPath, sandbox);

        AssertProjectSummary(describe, sandbox, includeRoot: true, includeSub: true);
        AssertProjectSummary(list, sandbox, includeRoot: true, includeSub: true);
        Golden.AssertMatches("proj-describe", Golden.Normalize(describe.Stdout, sandbox));
        Golden.AssertMatches("proj-describe", Golden.Normalize(list.Stdout, sandbox));
    }

    [Fact(DisplayName = "proj-describe-subtree: describe filters transpilers by cwd subtree")]
    public async Task DescribeFiltersTranspilersByCurrentSubtree()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-describe");
        var subPath = CreateDirectory(sandbox.ProjectPath, "sub");

        var result = await _cli.Run(["-describe"], subPath, sandbox);

        AssertProjectSummary(result, sandbox, includeRoot: false, includeSub: true);
    }

    [Fact(DisplayName = "proj-describe-all: describeAll and da ignore cwd filtering")]
    public async Task DescribeAllIgnoresCurrentSubtree()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-describe");
        var subPath = CreateDirectory(sandbox.ProjectPath, "sub");

        var describeAll = await _cli.Run(["-describeAll"], subPath, sandbox);
        var da = await _cli.Run(["da"], subPath, sandbox);

        AssertProjectSummary(describeAll, sandbox, includeRoot: true, includeSub: true);
        AssertProjectSummary(da, sandbox, includeRoot: true, includeSub: true);
    }

    [Theory(DisplayName = "describe-downstream: describe shows the subtree from cwd downstream")]
    [InlineData("describe")]
    [InlineData("-describe")]
    [InlineData("list")]
    public async Task DescribeShowsEverythingBelowTheCurrentFolder(string form)
    {
        using var sandbox = Sandbox.Create(_cli);
        WriteNestedDescribeProject(sandbox);
        var subPath = CreateDirectory(sandbox.ProjectPath, "sub");
        CreateDirectory(subPath, "deeper");

        var result = await _cli.Run([form], subPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("to-uppercase", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("echo", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("cat", result.Stdout, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "describe-local: describeLocal shows only the exact cwd")]
    [InlineData("describeLocal")]
    [InlineData("-describeLocal")]
    [InlineData("-dl")]
    public async Task DescribeLocalShowsOnlyTheExactFolder(string form)
    {
        using var sandbox = Sandbox.Create(_cli);
        WriteNestedDescribeProject(sandbox);
        var subPath = CreateDirectory(sandbox.ProjectPath, "sub");
        CreateDirectory(subPath, "deeper");

        var result = await _cli.Run([form], subPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("echo", result.Stdout, StringComparison.Ordinal);

        // Neither the parent step nor the descendant step is listed.
        Assert.DoesNotContain("to-uppercase", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("cat", result.Stdout, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "describe-with-subprojects: describeWithSubprojects includes nested projects")]
    [Trait("Slow", "true")]
    [InlineData("describeWithSubprojects")]
    [InlineData("-dws")]
    public async Task DescribeWithSubprojectsIncludesNestedProjects(string form)
    {
        using var sandbox = Sandbox.Create(_cli);
        WriteNestedDescribeProject(sandbox);
        var subPath = CreateDirectory(sandbox.ProjectPath, "sub");
        CreateDirectory(subPath, "deeper");
        var nested = CreateDirectory(sandbox.ProjectPath, "nested");
        WorkflowTestSupport.WriteProjectAt(
            nested,
            new WorkflowStep("Nested", "", "nested-tool -i n.txt"));

        var withSubprojects = await _cli.Run(
            [form],
            subPath,
            sandbox,
            timeoutMs: 180_000);
        var describeAll = await _cli.Run(["-describeAll"], subPath, sandbox);

        Assert.Equal(0, withSubprojects.ExitCode);
        Assert.Equal(0, describeAll.ExitCode);

        // Both ignore cwd and list the whole root project.
        Assert.Contains("to-uppercase", withSubprojects.Stdout, StringComparison.Ordinal);
        Assert.Contains("to-uppercase", describeAll.Stdout, StringComparison.Ordinal);

        // Only the WithSubprojects scope enters the nested project.
        Assert.Contains("nested-tool", withSubprojects.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-tool", describeAll.Stdout, StringComparison.Ordinal);
    }

    private static void WriteNestedDescribeProject(Sandbox sandbox) =>
        WorkflowTestSupport.WriteProject(
            sandbox,
            new WorkflowStep("Root Step", "", "to-uppercase -i root.txt"),
            new WorkflowStep("Sub Step", "/sub", "echo -i sub.txt"),
            new WorkflowStep("Deep Step", "/sub/deeper", "cat -i deep.txt"));

    [Fact(DisplayName = "proj-no-project-silent: legacy describe outside a project prints an error")]
    public async Task DescribeOutsideAProjectPinsLegacyErrorOutput()
    {
        using var sandbox = Sandbox.Create(_cli);

        var result = await _cli.Run(["-describe"], sandbox.ProjectPath, sandbox);

        Assert.True(result.Failed);
        Assert.Equal(
            """
            ERROR: No project found in this directory or any parent directory.

            Run `effortless -init` to create a new project in this directory.
            """,
            NormalizeNewlines(result.Combined).Trim());
    }

    [Fact(DisplayName = "proj-list-settings: listSettings and ls print project settings")]
    public async Task ListSettingsAndAliasPrintProjectSettings()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-settings");

        var longName = await _cli.Run(["-listSettings"], sandbox.ProjectPath, sandbox);
        var alias = await _cli.Run(["-ls"], sandbox.ProjectPath, sandbox);

        foreach (var result in new[] { longName, alias })
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("SETTINGS: ", result.Stdout);
            Assert.Contains("    - project-name = Settings Project", result.Stdout);
            Assert.Contains("    - a = 1", result.Stdout);
        }
    }

    [Fact(DisplayName = "proj-add-setting: addSetting adds and case-insensitively replaces")]
    public async Task AddSettingAddsAndCaseInsensitivelyReplaces()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-basic");

        var add = await _cli.Run(["-addSetting", "a=1"], sandbox.ProjectPath, sandbox);
        var replace = await _cli.Run(["-as", "A=2"], sandbox.ProjectPath, sandbox);
        var equals = await _cli.Run(["-as", "b=x=y"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, add.ExitCode);
        Assert.Equal(0, replace.ExitCode);
        Assert.Equal(0, equals.ExitCode);
        Assert.Contains("Added Setting: a: '1'", add.Stdout);

        var project = Assert.IsType<JsonObject>(sandbox.ProjectFile);
        var matchingA = Settings(project)
            .Where(setting => String(setting, "Name").Equals("a", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(matchingA);
        Assert.Equal("2", String(matchingA[0], "Value"));

        // Legacy Split/Join behavior removes every '=' after the first one.
        Assert.Equal("xy", SettingValue(project, "b"));
    }

    [Fact(DisplayName = "proj-add-setting-bad: legacy accepts =x as an empty-valued x setting")]
    public async Task AddSettingWithMissingNamePinsLegacyParsing()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-basic");

        var result = await _cli.Run(["-addSetting", "=x"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Added Setting: x: ''", result.Stdout);
        Assert.Equal(string.Empty, SettingValue(Assert.IsType<JsonObject>(sandbox.ProjectFile), "x"));
        Assert.DoesNotContain("Settings must be in the format of 'name=value'", result.Combined);
    }

    [Fact(DisplayName = "proj-add-setting-project-name: project-name setting renames project")]
    public async Task ProjectNameSettingRenamesProject()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-basic");

        var result = await _cli.Run(
            ["-as", "project-name=Zed"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        var project = Assert.IsType<JsonObject>(sandbox.ProjectFile);
        Assert.Equal("Zed", String(project, "Name"));
        Assert.Equal("Zed", SettingValue(project, "project-name"));
    }

    [Fact(DisplayName = "proj-remove-setting: removeSetting removes and missing fails")]
    public async Task RemoveSettingRemovesAndMissingSettingFails()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-settings");

        var remove = await _cli.Run(["-removeSetting", "a"], sandbox.ProjectPath, sandbox);
        var missing = await _cli.Run(["-rs", "missing"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, remove.ExitCode);
        Assert.Contains("Successfully Removed Setting: a: '1'", remove.Stdout);
        Assert.Null(FindSetting(Assert.IsType<JsonObject>(sandbox.ProjectFile), "a"));
        Assert.True(missing.Failed);
        Assert.Contains("Can't find matching setting: missing", missing.Combined);
    }

    [Fact(DisplayName = "proj-save-preserves-custom: save preserves custom transpiler properties")]
    public async Task SavePreservesCustomTranspilerProperties()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-save-custom");

        var result = await _cli.Run(["-as", "x=1"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        var project = Assert.IsType<JsonObject>(sandbox.ProjectFile);
        var step = Assert.IsType<JsonObject>(Assert.Single(Array(project, "ProjectTranspilers")));
        Assert.Equal("hand written", String(step, "Description"));
        Assert.True(step["Enabled"]?.GetValue<bool>());
    }

    [Fact(DisplayName = "proj-save-no-resurrect: upgrade does not resurrect PinnedVersion")]
    public async Task UpgradeDoesNotResurrectPinnedVersion()
    {
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        server.IndexJson = index.Json;
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedHome(index);
        sandbox.SeedProject("project-pinned");

        var result = await _cli.Run(
            ["upgrade", "to-uppercase"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        var project = Assert.IsType<JsonObject>(sandbox.ProjectFile);
        var step = Assert.IsType<JsonObject>(Assert.Single(Array(project, "ProjectTranspilers")));
        Assert.False(step.ContainsKey("PinnedVersion"));
        Assert.Equal("v2026.01.01.0001", String(step, "LastVersionUsed"));
        Assert.Contains("unpinned", result.Stdout);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "proj-save-format: save emits the legacy project JSON format")]
    public async Task SaveEmitsLegacyProjectJsonFormat()
    {
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedProject("project-save-format");

        var result = await _cli.Run(["-as", "x=1"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        var contents = sandbox.ReadFile("effortless.json");
        Assert.EndsWith(Environment.NewLine, contents);
        Assert.Contains($"{Environment.NewLine}  \"Name\":", contents);
        Assert.Contains($"{Environment.NewLine}    {{", contents);

        var project = Assert.IsType<JsonObject>(sandbox.ProjectFile);
        Assert.False(project.ContainsKey("RootPath"));
        Assert.False(project.ContainsKey("ExpandedPaths"));
        Assert.False(project.ContainsKey("HiddenPaths"));
        Assert.False(project["ShowHidden"]?.GetValue<bool>());
        Assert.False(project["ShowAllFiles"]?.GetValue<bool>());
        Assert.True(project.ContainsKey("CurrentPath"));
        Assert.Null(project["CurrentPath"]);
        Assert.True(project.ContainsKey("SSoTmeProjectFiles"));
        Assert.Null(project["SSoTmeProjectFiles"]);

        foreach (var setting in Settings(project))
        {
            Assert.False(setting.ContainsKey("ProjectSettingId"));
        }

        foreach (var step in Array(project, "ProjectTranspilers").Select(Assert.IsType<JsonObject>))
        {
            Assert.False(step.ContainsKey("ProjectTranspilerId"));
            Assert.False(step.ContainsKey("SSoTmeProjectId"));
            Assert.True(step.ContainsKey("IsDisabled"));
            Assert.True(step.ContainsKey("IsSSoTTranspiler"));
        }
    }

    [Fact(DisplayName = "proj-save-order: replacing an installed step keeps its index")]
    public async Task ReplacingInstalledStepKeepsItsIndex()
    {
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        server.IndexJson = index.Json;
        server.Enqueue("echo", ToolBehavior.Files());
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedHome(index);
        var urls = index.ToolUrls.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        urls["third-tool"] =
            server.ToolUri("third-tool").ToString();
        ToolUrlTestSupport.WriteToolUrls(sandbox, urls);
        sandbox.SeedProject("project-order");

        var result = await _cli.Run(
            ["echo", "-install", "-p", "new=value"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        var project = Assert.IsType<JsonObject>(sandbox.ProjectFile);
        var commands = Array(project, "ProjectTranspilers")
            .Select(Assert.IsType<JsonObject>)
            .Select(step => String(step, "CommandLine"))
            .ToArray();
        Assert.Equal(3, commands.Length);
        Assert.StartsWith("to-uppercase ", commands[0]);
        Assert.StartsWith("echo ", commands[1]);
        Assert.Contains("new=value", commands[1]);
        Assert.StartsWith("third-tool ", commands[2]);
        Assert.Contains("Replaced Transpiler", result.Stdout);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "meta-debug-banner: -debug announces itself and the POST URL")]
    public async Task DebugAnnouncesItselfAndPostUrl()
    {
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        server.IndexJson = index.Json;
        server.Enqueue("to-uppercase", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(_cli);
        sandbox.SeedHome(index);
        sandbox.SeedProject("project-debug");

        var result = await _cli.Run(
            ["to-uppercase", "-i", "in.txt", "-debug"],
            sandbox.ProjectPath,
            sandbox);
        var request = await server.WaitForRequestAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("DEBUG OUTPUT ENABLED", result.Stdout);
        Assert.Contains($"POST {server.ToolUri("to-uppercase")}", result.Stdout);
        Assert.True(request.CliDebug);
        Assert.Equal(
            sandbox.ReadFile("in.txt").ToUpperInvariant(),
            sandbox.ReadFile("Output.txt"));
        server.ThrowIfFaulted();
    }

    private static void AssertProjectSummary(
        CliResult result,
        Sandbox sandbox,
        bool includeRoot,
        bool includeSub)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("======  Describe Project", result.Stdout);
        Assert.Contains($"======    {CliReportedPath(sandbox.ProjectPath)}", result.Stdout);
        Assert.Contains("SETTINGS: ", result.Stdout);
        Assert.Contains("    - a = 1", result.Stdout);

        if (includeRoot)
        {
            Assert.Contains("---- Root Step", result.Stdout);
            Assert.Contains("Command Line:> effortless to-uppercase -i root.txt", result.Stdout);
        }
        else
        {
            Assert.DoesNotContain("---- Root Step", result.Stdout);
            Assert.DoesNotContain("to-uppercase -i root.txt", result.Stdout);
        }

        if (includeSub)
        {
            Assert.Contains("---- Sub Step    **** DISABLED ****", result.Stdout);
            Assert.Contains("Command Line:> effortless echo -i sub.txt", result.Stdout);
        }
        else
        {
            Assert.DoesNotContain("---- Sub Step", result.Stdout);
            Assert.DoesNotContain("echo -i sub.txt", result.Stdout);
        }
    }

    private static string CreateDirectory(string root, params string[] segments)
    {
        var path = segments.Aggregate(root, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    private static JsonObject ReadObject(string path) =>
        Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidDataException($"JSON file '{path}' contains null."));

    private static JsonArray Array(JsonObject value, string propertyName) =>
        Assert.IsType<JsonArray>(value[propertyName]);

    private static string String(JsonObject value, string propertyName) =>
        value[propertyName]?.GetValue<string>()
        ?? throw new InvalidDataException($"JSON property '{propertyName}' is missing or null.");

    private static IEnumerable<JsonObject> Settings(JsonObject project) =>
        Array(project, "ProjectSettings").Select(Assert.IsType<JsonObject>);

    private static JsonObject? FindSetting(JsonObject project, string name) =>
        Settings(project)
            .SingleOrDefault(
                setting => String(setting, "Name").Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string SettingValue(JsonObject project, string name) =>
        String(
            FindSetting(project, name)
            ?? throw new Xunit.Sdk.XunitException($"Project setting '{name}' was not found."),
            "Value");

    private static string MinimalProjectJson(string name) =>
        $$"""
        {
          "Name": "{{name}}",
          "SSoTmeProjectId": "2f6f4055-4ec8-479e-aee8-07eea079a83f",
          "ProjectSettings": [
            {
              "Name": "project-name",
              "Value": "{{name}}"
            }
          ],
          "ProjectTranspilers": []
        }
        """;

    private static string CliReportedPath(string path) =>
        OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal)
            ? "/private" + path
            : path;

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private const string StandardGitIgnore = """
        /**/obj/**/*
        /**/bin/**/*
        /**/.effortless/**/*
        /**/.ssotme/**/*
        /**/.vs/**/*
        /**/node_modules/**/*
        /**/.vscode/**/*
        ssotme.env
        effortless.env
        """;
}
