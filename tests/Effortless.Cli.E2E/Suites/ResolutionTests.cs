using System.Text.Json.Nodes;
using Effortless.Cli.E2E.Harness;

namespace Effortless.Cli.E2E.Suites;

public sealed class ResolutionTests
{
    [Fact(DisplayName = "res-head-latest: head version resolves with [latest]")]
    public async Task HeadVersionResolvesAsLatest()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        server.Enqueue("to-uppercase", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(
            ["to-uppercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(
            result.ExitCode == 0,
            result.Combined);
        var request = Assert.Single(server.Requests);
        Assert.Equal("to-uppercase", request.ToolName);
        Assert.Equal(ResolutionTestSupport.HeadVersion, request.Version);
        Assert.Contains(
            $"cli:> effortless/common/to-uppercase {ResolutionTestSupport.HeadVersion} [latest]",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "res-explicit-version: explicit /vX")]
    public async Task ExplicitVersionResolvesRequestedVersionWithoutSuffix()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        server.Enqueue("to-uppercase", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(
            [$"to-uppercase/{ResolutionTestSupport.OldVersion}", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(result.ExitCode == 0, result.Combined);
        var request = Assert.Single(server.Requests);
        Assert.Equal(ResolutionTestSupport.OldVersion, request.Version);
        Assert.Contains(
            $"cli:> effortless/common/to-uppercase {ResolutionTestSupport.OldVersion}",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"{ResolutionTestSupport.OldVersion} [",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "res-explicit-missing: explicit version missing")]
    public async Task ExplicitMissingVersionPrintsSpecificErrorOnly()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(
            ["to-uppercase/v7", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "Error: version 'v7' not found for tool 'effortless/common/to-uppercase'.",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(ResolutionTestSupport.HeadVersion, result.Stdout, StringComparison.Ordinal);
        Assert.Contains(ResolutionTestSupport.OldVersion, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR: Tool", result.Stdout, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    [Fact(DisplayName = "res-pinned-labels: pinned labels")]
    public async Task LegacyDirectRunsResolveBeforeLoadingProjectPins()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        server.Enqueue(
            "to-uppercase",
            ToolBehavior.Echo(),
            ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);

        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "To Uppercase",
                "",
                "to-uppercase -i in.txt",
                ResolutionTestSupport.OldVersion));
        var oldPin = await cli.Run(
            ["to-uppercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "To Uppercase",
                "",
                "to-uppercase -i in.txt",
                ResolutionTestSupport.HeadVersion));
        var headPin = await cli.Run(
            ["to-uppercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, oldPin.ExitCode);
        Assert.Equal(0, headPin.ExitCode);
        Assert.Contains(
            $"{ResolutionTestSupport.HeadVersion} [latest]",
            oldPin.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{ResolutionTestSupport.HeadVersion} [latest]",
            headPin.Stdout,
            StringComparison.Ordinal);
        Assert.Equal(
            [ResolutionTestSupport.HeadVersion, ResolutionTestSupport.HeadVersion],
            server.Requests.Select(request => request.Version));
    }

    [Theory(DisplayName = "pin-version: pin fixes a step to a catalog version")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinFixesAStepToACatalogVersion(bool bareword)
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "To Uppercase",
                "",
                "to-uppercase -i in.txt"));

        string[] form = bareword
            ? ["pin", "to-uppercase", ResolutionTestSupport.OldVersion]
            : ["to-uppercase", "-pin", ResolutionTestSupport.OldVersion];
        var result = await cli.Run(form, sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            $"Pinned to-uppercase to {ResolutionTestSupport.OldVersion}",
            result.Stdout,
            StringComparison.Ordinal);

        var step = ResolutionTestSupport.FindStep(
            ResolutionTestSupport.ReadProject(sandbox),
            "to-uppercase");
        Assert.Equal(
            ResolutionTestSupport.OldVersion,
            step["PinnedVersion"]?.GetValue<string>());

        // Pinning is a project edit, not a run.
        Assert.Empty(server.Requests);
    }

    [Fact(DisplayName = "pin-url: pin accepts a literal URL")]
    public async Task PinAcceptsALiteralUrl()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "To Uppercase",
                "",
                "to-uppercase -i in.txt"));
        var url = server.ToolUri(
            "to-uppercase",
            ResolutionTestSupport.OldVersion).ToString();

        var result = await cli.Run(
            ["to-uppercase", "-pin", url],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            $"Pinned to-uppercase to {url}",
            result.Stdout,
            StringComparison.Ordinal);

        // A version key and a URL are stored the same way.
        var step = ResolutionTestSupport.FindStep(
            ResolutionTestSupport.ReadProject(sandbox),
            "to-uppercase");
        Assert.Equal(url, step["PinnedVersion"]?.GetValue<string>());
        Assert.Empty(server.Requests);
    }

    [Fact(DisplayName = "pin-then-build-uses-pin: a pinned step builds against the pin, not HEAD")]
    public async Task PinnedStepBuildsAgainstThePinRatherThanHead()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        server.Enqueue("to-uppercase", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "To Uppercase",
                "",
                "to-uppercase -i in.txt"));

        var pin = await cli.Run(
            ["to-uppercase", "-pin", ResolutionTestSupport.OldVersion],
            sandbox.ProjectPath,
            sandbox);
        Assert.Equal(0, pin.ExitCode);

        var build = await cli.Run(["build"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, build.ExitCode);

        // D17: the automatic freshness gate must not silently advance a
        // deliberately pinned step to HEAD.
        Assert.Equal(
            ResolutionTestSupport.OldVersion,
            Assert.Single(server.Requests).Version);
        Assert.Contains(
            ResolutionTestSupport.OldVersion,
            build.Stdout,
            StringComparison.Ordinal);

        var step = ResolutionTestSupport.FindStep(
            ResolutionTestSupport.ReadProject(sandbox),
            "to-uppercase");
        Assert.Equal(
            ResolutionTestSupport.OldVersion,
            step["PinnedVersion"]?.GetValue<string>());
    }

    [Fact(DisplayName = "latest-flag-rejected: -latest is no longer an option")]
    public async Task LatestFlagIsRejectedAsAnUnknownOption()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "To Uppercase",
                "",
                "to-uppercase -i in.txt"));

        var result = await cli.Run(
            ["to-uppercase", "-i", "in.txt", "-latest"],
            sandbox.ProjectPath,
            sandbox);

        // D17: -latest is drop-legacy, so it fails exactly like any other
        // unrecognized flag (see meta-unknown-option).
        Assert.True(result.Failed);
        Assert.Contains("latest", result.Combined, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(server.Requests);
    }

    [Fact(DisplayName = "acct-tool-falls-back-to-account: acct/tool resolves tool with -account acct")]
    public async Task CompoundAccountToolNameFallsBackToTheBareTool()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        server.Enqueue("to-uppercase", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.SeedProject(sandbox);

        // "acme/to-uppercase" is not a catalog key, but "to-uppercase" is.
        var result = await cli.Run(
            ["acme/to-uppercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        var request = Assert.Single(server.Requests);
        Assert.Equal(ResolutionTestSupport.HeadVersion, request.Version);
        Assert.Equal("acme", request.CliAccount);
        Assert.DoesNotContain("does not exist", result.Combined, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "acct-tool-unresolved-tool-fails: acct/tool fails when the tool is unknown too")]
    public async Task CompoundAccountToolNameStillFailsWhenTheBareToolIsUnknown()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(
            ["acme/nonexistent", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        // D20: the fallback is not a licence to succeed silently.
        Assert.True(result.Failed);
        Assert.Contains("does not exist", result.Combined, StringComparison.Ordinal);
        Assert.Contains("effortless listTools", result.Combined, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    [Fact(DisplayName = "res-no-head: tool without a head version")]
    public async Task NoHeadPrintsSpecificErrorButLegacyContinuesToUserOverride()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server, "resolution-no-head");
        server.Enqueue("no-head-override", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.WriteToolUrls(
            sandbox,
            new Dictionary<string, string>
            {
                ["cli-cloud-bridge"] = index.BridgeUri.ToString(),
                ["to-uppercase"] = server.ToolUri("no-head-override").ToString(),
            });
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(
            ["to-uppercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "Error: this tool has no head versions; please specify a version to run via effortless to-uppercase/version.",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("cli:> to-uppercase [user-set]", result.Stdout, StringComparison.Ordinal);
        Assert.Equal("no-head-override", Assert.Single(server.Requests).ToolName);
    }

    [Fact(DisplayName = "res-ambiguous: ambiguous short name prefers effortless/")]
    public async Task AmbiguousShortNamePrefersEffortlessAccount()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server, "resolution-ambiguous");
        server.Enqueue("effortless-echo", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(["echo", "-i", "in.txt"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "Warning: 'echo' matched multiple tools: acme/x/echo, effortless/y/echo. Using 'effortless/y/echo'.",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Equal("effortless-echo", Assert.Single(server.Requests).ToolName);
    }

    [Fact(DisplayName = "res-qualified: qualified names")]
    public async Task QualifiedNameSelectsExactToolWithoutWarning()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server, "resolution-ambiguous");
        server.Enqueue("acme-echo", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(
            ["acme/x/echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("matched multiple tools", result.Stdout, StringComparison.Ordinal);
        Assert.Equal("acme-echo", Assert.Single(server.Requests).ToolName);
    }

    [Fact(DisplayName = "res-refresh-on-miss: a missing tool triggers one refresh")]
    public async Task MissingToolInNonEmptyLegacyCacheDoesNotTriggerRefresh()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var staleIndex = IndexFixture.Load(toolServer);
        var refreshedIndex = IndexFixture.Load(toolServer, "resolution-refreshed");
        bridge.IndexJson = refreshedIndex.Json;
        toolServer.Enqueue("missing-tool-override", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, staleIndex, bridge.BridgeUri);
        ResolutionTestSupport.WriteToolUrls(
            sandbox,
            new Dictionary<string, string>
            {
                ["cli-cloud-bridge"] = bridge.BridgeUri.ToString(),
                ["to-lowercase"] = toolServer.ToolUri("missing-tool-override").ToString(),
            });
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(
            ["to-lowercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("CLOUD-BRIDGE CALL TRIGGERED", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Refreshing CLI tool URL index", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            "cli:> to-lowercase [user-set]",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Empty(bridge.Requests);
        Assert.Equal("missing-tool-override", Assert.Single(toolServer.Requests).ToolName);
    }

    [Fact(DisplayName = "res-refresh-empty-index: empty index triggers a refresh")]
    public async Task EmptyIndexTriggersRefreshAndThenRunsTool()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = index.Json;
        toolServer.Enqueue("to-uppercase", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);
        sandbox.WriteHomeFile(
            ".effortless/remote_tools/effortless-tools.json",
            """{"transpilerVersions":{}}""");
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(
            ["to-uppercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(
            result.ExitCode == 0,
            result.Combined
            + Environment.NewLine
            + File.ReadAllText(
                Path.Combine(
                    sandbox.HomePath,
                    ".effortless",
                    "remote_tools",
                    "effortless-tools.json")));
        Assert.Contains(
            "CLOUD-BRIDGE CALL TRIGGERED: Remote tools catalog freshness required",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            "[cli] Refreshing CLI tool URL index...",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Single(bridge.Requests);
        Assert.Single(toolServer.Requests);
    }

    [Fact(DisplayName = "res-bootstrap-first-run: first run bootstraps the sandbox home")]
    public async Task FirstRunBootstrapsHomeThroughConfiguredLocalBridge()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = index.Json;
        toolServer.Enqueue("to-uppercase", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedProject(sandbox);

        var configure = await cli.Run(
            ["-setUrl", $"cli-cloud-bridge={bridge.BridgeUri}"],
            sandbox.ProjectPath,
            sandbox);
        var result = await cli.Run(
            ["to-uppercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, configure.ExitCode);
        Assert.True(result.ExitCode == 0, result.Combined);
        Assert.Contains(
            "CLOUD-BRIDGE CALL TRIGGERED: Remote tools catalog freshness required",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Single(bridge.Requests);
        Assert.Single(toolServer.Requests);

        var configRoot = Path.Combine(sandbox.HomePath, ".effortless");
        Assert.True(File.Exists(Path.Combine(configRoot, "tool_urls.json")));
        Assert.True(File.Exists(Path.Combine(configRoot, "remote_tools", "effortless-tools.json")));
        Assert.True(File.Exists(Path.Combine(configRoot, "remote_tools", "cli_version")));
        Assert.True(File.Exists(Path.Combine(configRoot, "remote_tools", "effortless.json")));
    }

    [Fact(
        DisplayName = "res-dead-bridge-recovery: dead bridge resets to bootstrap URL",
        Skip = "The legacy DLL retries a hardcoded external HTTPS bootstrap URL; deterministic CI requires a product seam that Step 1 forbids.")]
    [Trait("Slow", "true")]
    public void DeadBridgeRecoveryRequiresTheHardcodedExternalService()
    {
    }

    [Fact(DisplayName = "res-no-refresh-on-version-change: CLI version change alone does not refresh")]
    public async Task VersionMarkerMismatchAloneDoesNotRefresh()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = index.Json;
        toolServer.Enqueue("to-uppercase", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);
        sandbox.WriteHomeFile(".effortless/remote_tools/cli_version", "0000");
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(
            ["to-uppercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(result.ExitCode == 0, result.Combined);
        Assert.DoesNotContain("CLOUD-BRIDGE CALL TRIGGERED", result.Stdout, StringComparison.Ordinal);
        Assert.Empty(bridge.Requests);
        Assert.Single(toolServer.Requests);
        Assert.Equal(
            "0000",
            File.ReadAllText(
                Path.Combine(
                    sandbox.HomePath,
                    ".effortless",
                    "remote_tools",
                    "cli_version")));
    }

    [Fact(DisplayName = "res-refresh-tools: refreshTools re-fetches")]
    public async Task RefreshToolsAliasesPurgeAndRefetchIndex()
    {
        foreach (var alias in new[] { "-refreshTools", "refreshtools", "-rt" })
        {
            var cli = new CliUnderTest();
            await using var toolServer = new MockToolServer();
            await using var bridge = new ResolutionBridgeServer();
            var index = IndexFixture.Load(toolServer);
            bridge.IndexJson = index.Json;
            using var sandbox = Sandbox.Create(cli);
            ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);
            sandbox.WriteHomeFile(".effortless/bridge_version_index", "9");

            var result = await cli.Run([alias], sandbox.ProjectPath, sandbox);

            Assert.True(result.ExitCode == 0, result.Combined);
            Assert.Contains(
                "CLOUD-BRIDGE CALL TRIGGERED: RefreshRemoteTools: explicit -refreshTools invocation",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Contains(
                "[cli] Remote tools index refreshed.",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Single(bridge.Requests);
            Assert.False(
                File.Exists(
                    Path.Combine(sandbox.HomePath, ".effortless", "bridge_version_index")));
            var persisted = File.ReadAllText(
                Path.Combine(
                    sandbox.HomePath,
                    ".effortless",
                    "remote_tools",
                    "effortless-tools.json"));
            Assert.Contains(
                "effortless/common/to-uppercase",
                persisted,
                StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "res-bridge-self-update: latestBridgeVersion updates tool_urls")]
    public async Task BridgeSelfUpdateAdvancesButDoesNotDowngradeItsIndex()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = WithBridgeVersion(index.Json, bridge.BridgeUri, 11);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);

        var upgraded = await cli.Run(["-refreshTools"], sandbox.ProjectPath, sandbox);

        Assert.True(
            upgraded.ExitCode == 0,
            upgraded.Combined);
        Assert.Equal(
            bridge.BridgeUri.ToString(),
            ResolutionTestSupport.ReadHomeObject(
                sandbox,
                ".effortless/tool_urls.json")["cli-cloud-bridge"]?.GetValue<string>());
        Assert.Equal(
            "11",
            File.ReadAllText(
                Path.Combine(sandbox.HomePath, ".effortless", "bridge_version_index")));

        bridge.IndexJson = WithBridgeVersion(index.Json, bridge.BridgeUri, 10);
        sandbox.WriteHomeFile(
            ".effortless/remote_tools/effortless-tools.json",
            """{"transpilerVersions":{}}""");
        ResolutionTestSupport.SeedProject(sandbox);
        toolServer.Enqueue("to-uppercase", ToolBehavior.Echo());

        var lower = await cli.Run(
            ["to-uppercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(
            lower.ExitCode == 0,
            lower.Combined);
        Assert.Equal(2, bridge.Requests.Count);
        Assert.Equal(
            "11",
            File.ReadAllText(
                Path.Combine(sandbox.HomePath, ".effortless", "bridge_version_index")));
    }

    [Fact(DisplayName = "res-bridge-payload: bridge is called with cli_version")]
    public async Task BridgeRefreshPayloadIncludesCliVersionAndRemoteToolsProjectName()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = index.Json;
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);

        var result = await cli.Run(["-refreshTools"], sandbox.ProjectPath, sandbox);

        Assert.True(
            result.ExitCode == 0,
            result.Combined);
        var request = Assert.Single(bridge.Requests);
        Assert.Contains(
            $"cli_version={CliUnderTest.PackageVersion}",
            request.CliParams);
        Assert.Contains("project-name=remote_tools", request.CliParams);
        Assert.NotNull(request.CliTranspiler);
        Assert.NotNull(request.TranspilerName);
    }

    [Fact(DisplayName = "res-bridge-list-transpilers-removed: refresh drops the legacy list-transpilers mapping")]
    public async Task RefreshRemovesLegacyListTranspilersMapping()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = index.Json;
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);
        var urls = ResolutionTestSupport.ReadHomeObject(sandbox, ".effortless/tool_urls.json");
        urls["list-transpilers"] = "http://example.invalid/legacy";
        sandbox.WriteHomeFile(".effortless/tool_urls.json", urls.ToJsonString());

        var result = await cli.Run(["-refreshTools"], sandbox.ProjectPath, sandbox);

        Assert.True(result.ExitCode == 0, result.Combined);
        var afterUrls = ResolutionTestSupport.ReadHomeObject(sandbox, ".effortless/tool_urls.json");
        Assert.Null(afterUrls["list-transpilers"]);
    }

    [Fact(DisplayName = "res-update-available-file: bridge cliUpdateAvailable is written and cleared")]
    public async Task BridgeUpdateAvailableIsWrittenThenCleared()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = WithUpdateAvailable(index.Json, "2026.09.01.0001");
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);

        var withUpdate = await cli.Run(["-refreshTools"], sandbox.ProjectPath, sandbox);

        Assert.True(withUpdate.ExitCode == 0, withUpdate.Combined);
        var updatePath = Path.Combine(sandbox.HomePath, ".effortless", "update_available.json");
        Assert.True(File.Exists(updatePath));
        var written = JsonNode.Parse(File.ReadAllText(updatePath))!.AsObject();
        Assert.Equal("2026.09.01.0001", written["name"]?.GetValue<string>());

        bridge.IndexJson = index.Json;
        sandbox.WriteHomeFile(".effortless/remote_tools/effortless-tools.json", """{"transpilerVersions":{}}""");
        ResolutionTestSupport.SeedProject(sandbox);
        toolServer.Enqueue("to-uppercase", ToolBehavior.Echo());

        var withoutUpdate = await cli.Run(["to-uppercase", "-i", "in.txt"], sandbox.ProjectPath, sandbox);

        Assert.True(withoutUpdate.ExitCode == 0, withoutUpdate.Combined);
        Assert.False(File.Exists(updatePath));
    }

    [Fact(DisplayName = "res-list-versions: listVersions output")]
    public async Task ListVersionsAliasesShowSortedVersionsAndOverride()
    {
        foreach (var alias in new[] { "-listVersions", "-lv", "-list", "-l" })
        {
            var cli = new CliUnderTest();
            await using var server = new MockToolServer();
            var index = IndexFixture.Load(server);
            using var sandbox = Sandbox.Create(cli);
            ResolutionTestSupport.SeedHome(sandbox, index);
            ResolutionTestSupport.WriteToolUrls(
                sandbox,
                new Dictionary<string, string>
                {
                    ["cli-cloud-bridge"] = index.BridgeUri.ToString(),
                    ["to-uppercase"] = "http://localhost:43210/local/",
                });

            var result = await cli.Run(
                ["to-uppercase", alias],
                sandbox.ProjectPath,
                sandbox);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(
                "Available versions for effortless/common/to-uppercase:",
                result.Stdout,
                StringComparison.Ordinal);
            var headIndex = result.Stdout.IndexOf(
                $"  {ResolutionTestSupport.HeadVersion} (latest)",
                StringComparison.Ordinal);
            var oldIndex = result.Stdout.IndexOf(
                $"  {ResolutionTestSupport.OldVersion}",
                StringComparison.Ordinal);
            Assert.True(headIndex >= 0 && oldIndex > headIndex, result.Stdout);
            Assert.Contains(
                $"url: {server.ToolUri("to-uppercase", ResolutionTestSupport.HeadVersion)}",
                result.Stdout,
                StringComparison.Ordinal);
            // D16: the hint names the canonical *ToolUrl verbs after the rename.
            Assert.Contains(
                "* globally overridden via effortless -setToolUrl to-uppercase=http://localhost:43210/local/",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Contains(
                "run 'effortless -removeToolUrl to-uppercase' to reset",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Contains(
                "Run with: effortless to-uppercase/<versionKey>",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Contains(
                "Run latest: effortless to-uppercase",
                result.Stdout,
                StringComparison.Ordinal);
            Golden.AssertMatches(
                "res-list-versions",
                Golden.Normalize(result.Stdout, sandbox, server.BaseUri.ToString()));
        }
    }

    [Fact(DisplayName = "res-list-versions-missing: listVersions miss")]
    public async Task ListVersionsMissingPrintsLegacySystemHint()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);

        var result = await cli.Run(
            ["nope", "-listVersions"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "No versions for 'nope' were found in the remote tools index.",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "res-upgrade-tool: upgrade clears the pin")]
    public async Task UpgradeToolAliasesClearPinWithoutRunningTool()
    {
        var forms = new[]
        {
            new[] { "upgrade", "to-uppercase" },
            new[] { "to-uppercase", "-upgrade" },
            new[] { "unpin", "to-uppercase" },
        };

        foreach (var form in forms)
        {
            var cli = new CliUnderTest();
            await using var toolServer = new MockToolServer();
            await using var bridge = new ResolutionBridgeServer();
            var index = IndexFixture.Load(toolServer);
            bridge.IndexJson = index.Json;
            using var sandbox = Sandbox.Create(cli);
            ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);
            ResolutionTestSupport.SeedProject(
                sandbox,
                new ResolutionProjectStep(
                    "To Uppercase",
                    "",
                    "to-uppercase -i in.txt",
                    ResolutionTestSupport.OldVersion));

            var result = await cli.Run(form, sandbox.ProjectPath, sandbox);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(
                "CLOUD-BRIDGE CALL TRIGGERED: RefreshRemoteTools: explicit -refreshTools invocation",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Contains(
                $"Upgraded to-uppercase: {ResolutionTestSupport.OldVersion} → HEAD ({ResolutionTestSupport.HeadVersion}, unpinned — will track latest)",
                result.Stdout,
                StringComparison.Ordinal);
            var step = ResolutionTestSupport.FindStep(
                ResolutionTestSupport.ReadProject(sandbox),
                "to-uppercase");
            Assert.Null(step["PinnedVersion"]);
            Assert.Equal(
                ResolutionTestSupport.HeadVersion,
                step["LastVersionUsed"]?.GetValue<string>());
            Assert.Empty(toolServer.Requests);
            Assert.Single(bridge.Requests);
        }
    }

    [Fact(DisplayName = "res-upgrade-cwd-disambiguation: upgrade from a subdirectory unpins only that step")]
    public async Task UpgradeFromSubdirectoryUnpinsOnlyThatStep()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = index.Json;
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);
        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "Root",
                "",
                "to-uppercase -i in.txt",
                ResolutionTestSupport.OldVersion),
            new ResolutionProjectStep(
                "Sub",
                "/sub",
                "to-uppercase -i in.txt",
                ResolutionTestSupport.OldVersion));
        var sub = Directory.CreateDirectory(Path.Combine(sandbox.ProjectPath, "sub")).FullName;

        var result = await cli.Run(["upgrade", "to-uppercase"], sub, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            $"Upgraded to-uppercase: {ResolutionTestSupport.OldVersion} → HEAD ({ResolutionTestSupport.HeadVersion}, unpinned — will track latest)",
            result.Stdout,
            StringComparison.Ordinal);
        var project = ResolutionTestSupport.ReadProject(sandbox);
        var steps = project["ProjectTranspilers"]!.AsArray();
        var rootStep = steps.Select(node => node!.AsObject())
            .Single(step => step["RelativePath"]?.GetValue<string>() == "");
        var subStep = steps.Select(node => node!.AsObject())
            .Single(step => step["RelativePath"]?.GetValue<string>() == "/sub");
        Assert.Equal(ResolutionTestSupport.OldVersion, rootStep["PinnedVersion"]?.GetValue<string>());
        Assert.Null(subStep["PinnedVersion"]);
        Assert.Equal(ResolutionTestSupport.HeadVersion, subStep["LastVersionUsed"]?.GetValue<string>());
    }

    [Fact(DisplayName = "res-upgrade-not-installed: upgrade of an uninstalled tool")]
    public async Task UpgradeUninstalledToolRefreshesAndExitsSuccessfully()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = index.Json;
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(
            ["upgrade", "to-uppercase"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "to-uppercase is not used in this project — nothing to unpin here.",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Refreshed the core tools index; 'to-uppercase' will track latest (HEAD {ResolutionTestSupport.HeadVersion}) wherever it is used unpinned.",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Single(bridge.Requests);
        Assert.Empty(toolServer.Requests);
    }

    [Fact(DisplayName = "res-upgrade-override-warning: upgrade warns when tool_urls.json overrides the tool")]
    public async Task UpgradeWarnsWhenToolUrlsOverridesTheTool()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = index.Json;
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);
        var overrideUrl = "https://stale-to-uppercase.invalid/";
        ResolutionTestSupport.WriteToolUrls(
            sandbox,
            new Dictionary<string, string>
            {
                ["cli-cloud-bridge"] = bridge.BridgeUri.ToString(),
                ["to-uppercase"] = overrideUrl,
            });
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(
            ["upgrade", "to-uppercase"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            $"WARNING: 'to-uppercase' will NOT run HEAD ({ResolutionTestSupport.HeadVersion}): ~/.effortless/tool_urls.json overrides it with {overrideUrl}. Run 'effortless -removeToolUrl to-uppercase' to use HEAD.",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.DoesNotContain("will track latest", result.Stdout, StringComparison.Ordinal);
        Assert.Empty(toolServer.Requests);
    }

    [Fact(DisplayName = "res-upgrade-no-project: upgrade outside a project")]
    public async Task UpgradeOutsideProjectReportsNoProjectAfterRefresh()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = index.Json;
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);

        var result = await cli.Run(
            ["upgrade", "to-uppercase"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "No effortless.json project found in this directory.",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Single(bridge.Requests);
        Assert.Empty(toolServer.Requests);
    }

    [Fact(DisplayName = "res-upgrade-all: upgradeAll")]
    public async Task UpgradeAllAliasesReportUpOkAndSkipCounts()
    {
        var forms = new[]
        {
            new[] { "-upgradeAll" },
            new[] { "upgradeall" },
            new[] { "upgrade" },
        };

        foreach (var form in forms)
        {
            var cli = new CliUnderTest();
            await using var toolServer = new MockToolServer();
            await using var bridge = new ResolutionBridgeServer();
            var index = IndexFixture.Load(toolServer);
            bridge.IndexJson = index.Json;
            using var sandbox = Sandbox.Create(cli);
            ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);
            ResolutionTestSupport.SeedProject(
                sandbox,
                new ResolutionProjectStep(
                    "To Uppercase",
                    "",
                    "to-uppercase -i in.txt",
                    ResolutionTestSupport.OldVersion),
                new ResolutionProjectStep(
                    "Echo",
                    "",
                    "echo -i in.txt",
                    LastVersionUsed: ResolutionTestSupport.HeadVersion),
                new ResolutionProjectStep(
                    "Execute",
                    "",
                    "-execute echo local"),
                new ResolutionProjectStep(
                    "Unknown",
                    "",
                    "unknown-tool -i in.txt",
                    "v1"));

            var result = await cli.Run(form, sandbox.ProjectPath, sandbox);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(
                $"  UP   to-uppercase: {ResolutionTestSupport.OldVersion} → HEAD ({ResolutionTestSupport.HeadVersion}, unpinned)",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Contains(
                $"  OK   echo — already unpinned at HEAD ({ResolutionTestSupport.HeadVersion})",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.DoesNotContain("-execute", result.Stdout, StringComparison.Ordinal);
            Assert.Contains(
                "  SKIP unknown-tool — not found in remote tools index",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.True(
                result.Stdout.Contains(
                    "Upgraded 1 tool(s), skipped 1.",
                    StringComparison.Ordinal),
                result.Stdout);
            Assert.Single(bridge.Requests);
            Assert.Empty(toolServer.Requests);
        }
    }

    [Fact(DisplayName = "res-user-set-override: tool_urls override beats the index")]
    public async Task UserOverrideBeatsIndexAndPreservesResolvedVersionMetadata()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        var overrideUri = server.ToolUri("override-uppercase");
        server.Enqueue("override-uppercase", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        ResolutionTestSupport.WriteToolUrls(
            sandbox,
            new Dictionary<string, string>
            {
                ["cli-cloud-bridge"] = index.BridgeUri.ToString(),
                ["to-uppercase"] = overrideUri.ToString(),
            });
        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "To Uppercase",
                "",
                "to-uppercase -i in.txt",
                LastVersionUsed: ResolutionTestSupport.HeadVersion));

        var result = await cli.Run(
            ["to-uppercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        var request = Assert.Single(server.Requests);
        Assert.Equal("override-uppercase", request.ToolName);
        Assert.Contains("cli:> to-uppercase [user-set]", result.Stdout, StringComparison.Ordinal);
        var step = ResolutionTestSupport.FindStep(
            ResolutionTestSupport.ReadProject(sandbox),
            "to-uppercase");
        Assert.Equal(
            ResolutionTestSupport.HeadVersion,
            step["LastVersionUsed"]?.GetValue<string>());
    }

    [Fact(DisplayName = "res-upgrade-all-override: upgradeAll flags steps whose tool is overridden")]
    public async Task UpgradeAllFlagsOverriddenSteps()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = index.Json;
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);
        var overrideUrl = "https://stale-to-uppercase.invalid/";
        ResolutionTestSupport.WriteToolUrls(
            sandbox,
            new Dictionary<string, string>
            {
                ["cli-cloud-bridge"] = bridge.BridgeUri.ToString(),
                ["to-uppercase"] = overrideUrl,
            });
        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "To Uppercase",
                "",
                "to-uppercase -i in.txt",
                LastVersionUsed: ResolutionTestSupport.HeadVersion));

        var result = await cli.Run(["-upgradeAll"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            $"  OK   to-uppercase — already unpinned at HEAD ({ResolutionTestSupport.HeadVersion})",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            $"  OVERRIDE to-uppercase — runs {overrideUrl} from ~/.effortless/tool_urls.json, NOT HEAD; run 'effortless -removeToolUrl to-uppercase' to use HEAD",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            "Upgraded 0 tool(s), 1 overridden by tool_urls.json.",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Empty(toolServer.Requests);
    }

    [Fact(DisplayName = "res-user-set-offline: a dead tool_urls override is reported as the override, not the catalog")]
    public async Task DeadUserSetOverrideIsReportedAsTheOverride()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index);
        var overrideUrl = "https://stale-to-uppercase.invalid/";
        ResolutionTestSupport.WriteToolUrls(
            sandbox,
            new Dictionary<string, string>
            {
                ["cli-cloud-bridge"] = index.BridgeUri.ToString(),
                ["to-uppercase"] = overrideUrl,
            });
        ResolutionTestSupport.SeedProject(sandbox);
        sandbox.WriteFile("in.txt", "input");

        var result = await cli.Run(
            ["to-uppercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 30_000);

        Assert.True(result.Failed);
        Assert.Contains(
            $"to-uppercase is offline at {overrideUrl}. That URL is your tool_urls.json override, not the catalog",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            "Run 'effortless -removeToolUrl to-uppercase' to use the catalog.",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.DoesNotContain("catalog age", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("CLOUD-BRIDGE CALL TRIGGERED", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("*** TRANSPILER ERROR ***", result.Combined, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    [Fact(DisplayName = "res-user-set-unknown-tool: tool_urls-only tool")]
    public async Task UserSetUnknownToolResolvesWithoutLegacyRefresh()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = index.Json;
        var userUri = toolServer.ToolUri("mytool");
        toolServer.Enqueue("mytool", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(sandbox, index, bridge.BridgeUri);
        ResolutionTestSupport.WriteToolUrls(
            sandbox,
            new Dictionary<string, string>
            {
                ["cli-cloud-bridge"] = bridge.BridgeUri.ToString(),
                ["mytool"] = userUri.ToString(),
            });
        ResolutionTestSupport.SeedProject(sandbox);

        var result = await cli.Run(
            ["mytool", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("cli:> mytool [user-set]", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("CLOUD-BRIDGE CALL TRIGGERED", result.Stdout, StringComparison.Ordinal);
        Assert.Empty(bridge.Requests);
        Assert.Equal("mytool", Assert.Single(toolServer.Requests).ToolName);
    }

    [Fact(DisplayName = "res-list-tools: listTools projects sorted catalog HEADs")]
    public async Task ListToolsAliasesProjectSortedHeadsAndNoHead()
    {
        foreach (var alias in new[] { "listTools", "-listTools", "-lt" })
        {
            var cli = new CliUnderTest();
            await using var toolServer = new MockToolServer();
            await using var bridge = new ResolutionBridgeServer();
            var index = IndexFixture.Load(toolServer);
            using var sandbox = Sandbox.Create(cli);
            ResolutionTestSupport.SeedHome(
                sandbox,
                index,
                bridge.BridgeUri);
            AddNoHeadTool(sandbox, "aaa/example/no-head");

            var result = await cli.Run(
                [alias],
                sandbox.ProjectPath,
                sandbox);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(
                "Available tools (3):",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Contains(
                "  NAME",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Matches(
                @"  aaa/example/no-head\s+NO HEAD",
                result.Stdout);
            Assert.Matches(
                $@"  effortless/common/to-uppercase\s+{System.Text.RegularExpressions.Regex.Escape(ResolutionTestSupport.HeadVersion)}",
                result.Stdout);
            Assert.True(
                result.Stdout.IndexOf(
                    "aaa/example/no-head",
                    StringComparison.Ordinal)
                < result.Stdout.IndexOf(
                    "effortless/common/echo",
                    StringComparison.Ordinal));
            Assert.Empty(bridge.Requests);
        }
    }

    [Fact(DisplayName = "res-search-tools: searchTools matches canonical and short names")]
    public async Task SearchToolsMatchesCanonicalAndShortNames()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(
            sandbox,
            index,
            bridge.BridgeUri);

        var canonical = await cli.Run(
            ["searchTools", "COMMON/TO-UPPER"],
            sandbox.ProjectPath,
            sandbox);
        var shortName = await cli.Run(
            ["-st", "uppercase"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, canonical.ExitCode);
        Assert.Contains(
            "Tools matching 'COMMON/TO-UPPER' (1):",
            canonical.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            "effortless/common/to-uppercase",
            canonical.Stdout,
            StringComparison.Ordinal);
        Assert.Equal(0, shortName.ExitCode);
        Assert.Contains(
            "Tools matching 'uppercase' (1):",
            shortName.Stdout,
            StringComparison.Ordinal);
        Assert.Empty(bridge.Requests);
    }

    [Fact(DisplayName = "res-search-tools-empty: searchTools reports no matches explicitly")]
    public async Task SearchToolsReportsNoMatchesExplicitly()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(
            sandbox,
            index,
            bridge.BridgeUri);

        var result = await cli.Run(
            ["searchTools", "definitely-not-present"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            $"No tools matched 'definitely-not-present'.{Environment.NewLine}",
            result.Stdout);
        Assert.Empty(bridge.Requests);
    }

    [Fact(DisplayName = "res-freshness-current: a 23:59:59 catalog is reused")]
    public async Task FreshCatalogAndCurrentProjectAvoidRefreshAndSave()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(
            sandbox,
            index,
            bridge.BridgeUri);
        SetFetchedAt(
            sandbox,
            CliUnderTest.TestUtcNow
            - TimeSpan.FromHours(24)
            + TimeSpan.FromSeconds(1));
        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "Echo",
                "",
                "echo -i in.txt",
                LastVersionUsed:
                ResolutionTestSupport.HeadVersion,
                IsDisabled: true));
        var before = sandbox.ReadFile("effortless.json");

        var result = await cli.Run(
            ["build"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(
            result.ExitCode == 0,
            result.Combined
            + Environment.NewLine
            + File.ReadAllText(
                Path.Combine(
                    sandbox.HomePath,
                    ".effortless",
                    "remote_tools",
                    "effortless-tools.json")));
        Assert.Contains(
            "[cli] Project tools are current.",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Empty(bridge.Requests);
        Assert.Empty(toolServer.Requests);
        Assert.Equal(before, sandbox.ReadFile("effortless.json"));
    }

    [Fact(DisplayName = "res-freshness-stale-upgrades: stale refresh upgrades before build")]
    public async Task StaleCatalogRefreshesAndUpgradesBeforeBuild()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        bridge.IndexJson = index.Json;
        toolServer.Enqueue("to-uppercase", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(
            sandbox,
            index,
            bridge.BridgeUri);
        SetFetchedAt(
            sandbox,
            CliUnderTest.TestUtcNow
            - TimeSpan.FromHours(24));
        // The pin names a version the refreshed catalog does not carry, so it
        // is stale rather than deliberate: D17 honors only pins the catalog can
        // still satisfy, and step-03A's gate still clears the rest.
        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "To Uppercase",
                "",
                "to-uppercase -i in.txt",
                "v2020.01.01.0000",
                ResolutionTestSupport.OldVersion));

        var result = await cli.Run(
            ["build"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(bridge.Requests);
        Assert.Equal(
            ResolutionTestSupport.HeadVersion,
            Assert.Single(toolServer.Requests).Version);
        var step = ResolutionTestSupport.FindStep(
            ResolutionTestSupport.ReadProject(sandbox),
            "to-uppercase");
        Assert.Null(step["PinnedVersion"]);
        Assert.Equal(
            ResolutionTestSupport.HeadVersion,
            step["LastVersionUsed"]?.GetValue<string>());
    }

    [Fact(DisplayName = "res-freshness-invalid-time: unprovable timestamps refresh")]
    public async Task MissingMalformedAndFutureTimestampsRefresh()
    {
        foreach (var timestamp in new[]
                 {
                     (string?)null,
                     "not-a-time",
                     (CliUnderTest.TestUtcNow
                      + TimeSpan.FromMinutes(5)
                      + TimeSpan.FromSeconds(1)).ToString("O"),
                 })
        {
            var cli = new CliUnderTest();
            await using var toolServer = new MockToolServer();
            await using var bridge = new ResolutionBridgeServer();
            var index = IndexFixture.Load(toolServer);
            bridge.IndexJson = index.Json;
            using var sandbox = Sandbox.Create(cli);
            ResolutionTestSupport.SeedHome(
                sandbox,
                index,
                bridge.BridgeUri);
            SetFetchedAt(sandbox, timestamp);

            var result = await cli.Run(
                ["listTools"],
                sandbox.ProjectPath,
                sandbox);

            Assert.True(result.ExitCode == 0, result.Combined);
            Assert.Single(bridge.Requests);
        }
    }

    [Fact(DisplayName = "res-freshness-refresh-fails: mandatory refresh never falls back")]
    public async Task FailedMandatoryRefreshPreservesBytesAndDoesNotBuild()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer
        {
            StatusCode = 500,
        };
        var index = IndexFixture.Load(toolServer);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(
            sandbox,
            index,
            bridge.BridgeUri);
        SetFetchedAt(
            sandbox,
            CliUnderTest.TestUtcNow
            - TimeSpan.FromHours(24));
        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "To Uppercase",
                "",
                "to-uppercase -i in.txt",
                ResolutionTestSupport.OldVersion,
                ResolutionTestSupport.OldVersion));
        var indexPath = Path.Combine(
            sandbox.HomePath,
            ".effortless",
            "remote_tools",
            "effortless-tools.json");
        var beforeIndex = File.ReadAllBytes(indexPath);
        var beforeProject = sandbox.ReadFile("effortless.json");

        var result = await cli.Run(
            ["build"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(result.Failed);
        Assert.Contains(
            "Remote tools index refresh failed",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Single(bridge.Requests);
        Assert.Empty(toolServer.Requests);
        Assert.Equal(beforeIndex, File.ReadAllBytes(indexPath));
        Assert.Equal(
            beforeProject,
            sandbox.ReadFile("effortless.json"));
    }

    [Fact(DisplayName = "res-freshness-upgrade-atomic: project upgrades are all-or-nothing")]
    public async Task AutomaticUpgradeFailureDoesNotPartiallyMutateProject()
    {
        var cli = new CliUnderTest();
        await using var toolServer = new MockToolServer();
        await using var bridge = new ResolutionBridgeServer();
        var index = IndexFixture.Load(toolServer);
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.SeedHome(
            sandbox,
            index,
            bridge.BridgeUri);
        ResolutionTestSupport.SeedProject(
            sandbox,
            new ResolutionProjectStep(
                "To Uppercase",
                "",
                "to-uppercase -i in.txt",
                ResolutionTestSupport.OldVersion,
                ResolutionTestSupport.OldVersion),
            new ResolutionProjectStep(
                "Missing",
                "",
                "missing-remote-tool -i in.txt",
                "v0",
                "v0"));
        var before = sandbox.ReadFile("effortless.json");

        var result = await cli.Run(
            ["build"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(result.Failed);
        Assert.Contains(
            "missing-remote-tool",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Empty(bridge.Requests);
        Assert.Empty(toolServer.Requests);
        Assert.Equal(before, sandbox.ReadFile("effortless.json"));
    }

    [Fact(DisplayName = "res-freshness-offline-meta: offline metadata commands do not refresh")]
    public async Task OfflineMetadataCommandsDoNotRefreshMissingCatalog()
    {
        var cli = new CliUnderTest();
        await using var bridge = new ResolutionBridgeServer();
        using var sandbox = Sandbox.Create(cli);
        ResolutionTestSupport.WriteToolUrls(
            sandbox,
            new Dictionary<string, string>
            {
                ["cli-cloud-bridge"] = bridge.BridgeUri.ToString(),
            });

        var help = await cli.Run(["-help"], sandbox.ProjectPath, sandbox);
        var version = await cli.Run(["-version"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("Effortless CLI", help.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, version.ExitCode);
        Assert.Equal(CliUnderTest.DisplayVersion + Environment.NewLine, version.Stdout);
        Assert.Empty(bridge.Requests);
        Assert.False(
            File.Exists(
                Path.Combine(
                    sandbox.HomePath,
                    ".effortless",
                    "remote_tools",
                    "effortless-tools.json")));
    }

    private static string WithBridgeVersion(string indexJson, Uri bridgeUri, long versionIndex)
    {
        var root = JsonNode.Parse(indexJson)?.AsObject()
            ?? throw new InvalidDataException("Index fixture is not a JSON object.");
        root["cliUpdateAvailable"] = null;
        root["latestBridgeVersion"] = new JsonObject
        {
            ["name"] = "cli-cloud-bridge",
            ["version"] = $"v{versionIndex}",
            ["url"] = bridgeUri.ToString().TrimEnd('/'),
            ["versionIndex"] = versionIndex,
        };
        return root.ToJsonString();
    }

    private static string WithUpdateAvailable(string indexJson, string version)
    {
        var root = JsonNode.Parse(indexJson)?.AsObject()
            ?? throw new InvalidDataException("Index fixture is not a JSON object.");
        root["cliUpdateAvailable"] = new JsonObject
        {
            ["name"] = version,
            ["installLinks"] = new JsonObject(),
        };
        return root.ToJsonString();
    }

    private static void AddNoHeadTool(
        Sandbox sandbox,
        string canonicalName)
    {
        var root = ResolutionTestSupport.ReadHomeObject(
            sandbox,
            ".effortless/remote_tools/effortless-tools.json");
        var tools = root["transpilerVersions"]?.AsObject()
            ?? throw new InvalidDataException(
                "transpilerVersions is missing.");
        tools[canonicalName] = new JsonObject
        {
            ["v1"] = new JsonObject
            {
                ["metaData"] = new JsonObject
                {
                    ["isHeadVersion"] = false,
                },
                ["urls"] = new JsonObject
                {
                    ["post"] = "http://127.0.0.1:1/no-head/",
                },
            },
        };
        sandbox.WriteHomeFile(
            ".effortless/remote_tools/effortless-tools.json",
            root.ToJsonString());
    }

    private static void SetFetchedAt(
        Sandbox sandbox,
        DateTimeOffset value) =>
        SetFetchedAt(sandbox, value.ToString("O"));

    private static void SetFetchedAt(
        Sandbox sandbox,
        string? value)
    {
        var root = ResolutionTestSupport.ReadHomeObject(
            sandbox,
            ".effortless/remote_tools/effortless-tools.json");
        if (value is null)
        {
            root.Remove("fetchedAt");
        }
        else
        {
            root["fetchedAt"] = value;
        }

        sandbox.WriteHomeFile(
            ".effortless/remote_tools/effortless-tools.json",
            root.ToJsonString());
    }
}
