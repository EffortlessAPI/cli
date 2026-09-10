using System.Text;
using System.Text.Json;
using Effortless.Cli.E2E.Harness;

namespace Effortless.Cli.E2E.Suites;

public sealed class TranspileBehaviorTests
{
    [Fact(DisplayName = "tx-request-order-params: explicit parameters override settings and account parameters append last")]
    public async Task ExplicitParametersOverrideSettingsAndAccountParametersAppendLast()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "parameters");
        sandbox.WriteFile("effortless.env", "ACME_PAT=pat-from-env\n");
        server.Enqueue("echo", SuccessFiles());

        var result = await cli.Run(
            ["echo", "-i", "in.txt", "-p", "a=2", "-account", "acme"],
            sandbox.ProjectPath,
            sandbox);
        var request = await server.WaitForRequestAsync();

        AssertSuccess(result);
        Assert.Equal(
            ["a=2", "project-name=wire-project", "apiKey=pat-from-env"],
            request.CliParams);
        Assert.Single(request.CliParams, parameter => parameter.StartsWith("a=", StringComparison.Ordinal));
        Assert.Equal("apiKey=pat-from-env", request.CliParams[^1]);
        Assert.Equal("acme", request.CliAccount);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-input-rename: -i replacement changes the transmitted path but not contents")]
    public async Task InputRenameChangesTransmittedPathButNotContents()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("old.json", """{"source":"old"}""");
        server.Enqueue("echo", SuccessFiles());

        var result = await cli.Run(
            ["echo", "-i", "new.json=old.json"],
            sandbox.ProjectPath,
            sandbox);
        var request = await server.WaitForRequestAsync();

        AssertSuccess(result);
        var input = Assert.Single(request.InputFiles);
        Assert.Equal("new.json", input.RelativePath);
        Assert.Equal("""{"source":"old"}""", input.Text);
        Assert.Equal(FileSetContentKind.ZippedFileContents, input.Kind);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-input-list-glob: comma lists and repeated -i values preserve expansion order")]
    public async Task InputListsAndGlobsPreserveExpansionOrder()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("a.txt", "alpha");
        sandbox.WriteFile("b.txt", "bravo");
        sandbox.WriteFile("c.md", "charlie");
        server.Enqueue("echo", SuccessFiles());

        var result = await cli.Run(
            ["echo", "-i", "a.txt,*.md", "-i", "b.txt"],
            sandbox.ProjectPath,
            sandbox);
        var request = await server.WaitForRequestAsync();

        AssertSuccess(result);
        Assert.Equal(["a.txt,*.md", "b.txt"], request.CliInput);
        Assert.Equal(
            ["a.txt", "c.md", "b.txt"],
            request.InputFiles.Select(file => file.RelativePath).ToArray());
        Assert.Equal(
            ["alpha", "charlie", "bravo"],
            request.InputFiles.Select(file => file.Text).ToArray());
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-input-optional: legacy optional-input parse failure and required-input warning are pinned")]
    public async Task OptionalAndRequiredMissingInputsPinLegacyBehavior()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);

        var optional = await cli.Run(
            ["echo", "-i", "x.txt?"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, optional.ExitCode);
        Assert.Contains("Index was out of range", optional.Combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WARNING:", optional.Stdout, StringComparison.Ordinal);
        Assert.Empty(server.Requests);

        server.Enqueue("echo", SuccessFiles());
        var required = await cli.Run(
            ["echo", "-i", "x.txt"],
            sandbox.ProjectPath,
            sandbox);
        var request = await server.WaitForRequestAsync();

        AssertSuccess(required);
        Assert.Contains("WARNING:", required.Stdout, StringComparison.Ordinal);
        Assert.Contains("No INPUT files matched x.txt", required.Stdout, StringComparison.Ordinal);
        var emptyInput = Assert.Single(request.InputFiles);
        Assert.Equal("x.txt", emptyInput.RelativePath);
        Assert.Equal(FileSetContentKind.NoContent, emptyInput.Kind);
        Assert.Empty(emptyInput.Contents);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-input-outside-project: input paths outside project scope are rejected before POST")]
    public async Task InputOutsideProjectIsRejectedBeforePost()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        File.WriteAllText(Path.Combine(sandbox.RootPath, "outside.txt"), "not allowed");

        var result = await cli.Run(
            ["echo", "-i", "../outside.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.True(
            result.Combined.Contains("Access denied:", StringComparison.Ordinal),
            $"Expected access-denied details in:{Environment.NewLine}{result.Combined}");
        Assert.Contains("outside the current project scope", result.Combined, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-input-parent-project: child projects may read an input from their parent project")]
    public async Task ChildProjectCanReadInputFromParentProject()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("p.txt", "parent contents");
        sandbox.WriteFile("child/effortless.json", ProjectJson("child-project"));
        var childPath = Path.Combine(sandbox.ProjectPath, "child");
        server.Enqueue("echo", SuccessFiles());

        var result = await cli.Run(
            ["echo", "-i", "../p.txt"],
            childPath,
            sandbox);
        var request = await server.WaitForRequestAsync();

        AssertSuccess(result);
        Assert.Contains("Allowing access to parent effortless project:", result.Stdout, StringComparison.Ordinal);
        var input = Assert.Single(request.InputFiles);
        Assert.Equal("p.txt", input.RelativePath);
        Assert.Equal("parent contents", input.Text);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-input-binary: binary inputs use ZippedBinaryFileContents")]
    public async Task BinaryInputUsesZippedBinaryFileContents()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        var bytes = new byte[] { 0, 1, 2, 3, 255, 0, 128 };
        sandbox.WriteFile("logo.png", bytes);
        server.Enqueue("echo", SuccessFiles());

        var result = await cli.Run(
            ["echo", "-i", "logo.png"],
            sandbox.ProjectPath,
            sandbox);
        var request = await server.WaitForRequestAsync();

        AssertSuccess(result);
        var input = Assert.Single(request.InputFiles);
        Assert.Equal(FileSetContentKind.ZippedBinaryFileContents, input.Kind);
        Assert.Equal(bytes, input.Contents);
        Assert.Contains("<ZippedBinaryFileContents>", request.InputFileSetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ZippedFileContents>", request.InputFileSetXml, StringComparison.Ordinal);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-output-newuuid-cdata: output replaces UUID tokens and unwraps CDATA text")]
    public async Task OutputReplacesUuidTokensAndUnwrapsCdata()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Files(
                FileSetEntry.TextFile("g.txt", "id=[$$NEWUUID$$]", alwaysOverwrite: true),
                FileSetEntry.TextFile(
                    "h.txt",
                    "<![CDATA[decoded < value]]>",
                    alwaysOverwrite: true)));

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        AssertSuccess(result);
        var uuidText = sandbox.ReadFile("g.txt");
        Assert.StartsWith("id=", uuidText, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(uuidText["id=".Length..], out _), $"Expected a Guid in '{uuidText}'.");
        Assert.Equal("decoded < value", sandbox.ReadFile("h.txt"));
        Assert.DoesNotContain("CDATA", sandbox.ReadFile("h.txt"), StringComparison.Ordinal);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-output-no-content: output entries without content fail instead of creating files")]
    public async Task OutputEntryWithoutContentFails()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue("echo", ToolBehavior.Files(FileSetEntry.NoContent("x.txt")));

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(result.Failed);
        Assert.Contains(
            "Transpiler error: FileSet contains file entries without content nodes. Files: x.txt.",
            result.Combined,
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "x.txt")));
        Assert.Single(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-zfs-written: subdirectory runs write a gzip XML ledger under .effortless")]
    public async Task SubdirectoryRunWritesGzipXmlLedger()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        var subPath = Path.Combine(sandbox.ProjectPath, "sub");
        Directory.CreateDirectory(subPath);
        server.Enqueue(
            "echo",
            ToolBehavior.Files(FileSetEntry.TextFile("made.txt", "made", alwaysOverwrite: true)));

        var result = await cli.Run(
            ["echo", "-i", "../in.txt"],
            subPath,
            sandbox);

        AssertSuccess(result);
        var request = Assert.Single(server.Requests);
        var ledgerPath = Path.Combine(
            sandbox.ProjectPath,
            ".effortless",
            "sub",
            SanitizeUrl(server.ToolUri("echo").ToString()) + ".zfs");
        Assert.True(File.Exists(ledgerPath), $"Expected ledger '{ledgerPath}'.");
        var ledgerXml = Encoding.UTF8.GetString(FileSetXml.Gunzip(File.ReadAllBytes(ledgerPath)));
        var ledgerEntry = Assert.Single(FileSetXml.Parse(ledgerXml));
        Assert.Equal("made.txt", ledgerEntry.RelativePath);
        Assert.Equal("made", ledgerEntry.Text);
        Assert.Equal("in.txt", Assert.Single(request.InputFiles).RelativePath);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-zfs-self-source: an in-place upsert is downgraded to Never and survives, while a real output still cleans")]
    public async Task InPlaceUpsertSurvivesWhileGeneratedOutputStillCleans()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "original");

        // in.txt comes back at the path it was read from, declared Always: the in-place
        // upsert shape. out.txt is an ordinary generated file and must be unaffected.
        server.Enqueue(
            "echo",
            ToolBehavior.Files(
                FileSetEntry.TextFile("in.txt", "rewritten", alwaysOverwrite: true),
                FileSetEntry.TextFile("out.txt", "generated", alwaysOverwrite: true)),
            ToolBehavior.Files());

        var first = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        AssertSuccess(first);

        // The upsert still lands on disk even though it is recorded as Never.
        Assert.Equal("rewritten", File.ReadAllText(Path.Combine(sandbox.ProjectPath, "in.txt")));

        var ledgerPath = LedgerPath(sandbox, server, "echo");
        var firstLedger = ParseLedger(ledgerPath);
        Assert.Equal(
            ["in.txt", "out.txt"],
            firstLedger.Select(entry => entry.RelativePath).ToArray());

        // The whole fix: the input's own path is ledgered as Never, so nothing downstream
        // believes the CLI generated it; the real output keeps Always.
        var upsertEntry = firstLedger.Single(entry => entry.RelativePath == "in.txt");
        Assert.Equal("Never", upsertEntry.OverwriteMode);
        Assert.NotEqual(true, upsertEntry.AlwaysOverwrite);

        var generatedEntry = firstLedger.Single(entry => entry.RelativePath == "out.txt");
        Assert.Equal(true, generatedEntry.AlwaysOverwrite);

        // A hand-edit is the normal workflow for a file the user owns; it must not make
        // the next run delete it.
        sandbox.WriteFile("in.txt", "hand edited");

        var second = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        AssertSuccess(second);
        Assert.Equal("hand edited", File.ReadAllText(Path.Combine(sandbox.ProjectPath, "in.txt")));
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "out.txt")));
        Assert.Empty(ParseLedger(ledgerPath));
        Assert.Equal(2, server.Requests.Count);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-skip-clean: -sc preserves outputs recorded by the previous ledger")]
    public async Task SkipCleanPreservesPriorOutputs()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Files(FileSetEntry.TextFile("old.txt", "old", alwaysOverwrite: true)),
            ToolBehavior.Files(FileSetEntry.TextFile("new.txt", "new", alwaysOverwrite: true)));

        var first = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);
        var second = await cli.Run(
            ["echo", "-i", "in.txt", "-sc"],
            sandbox.ProjectPath,
            sandbox);

        AssertSuccess(first);
        AssertSuccess(second);
        Assert.Equal("old", sandbox.ReadFile("old.txt"));
        Assert.Equal("new", sandbox.ReadFile("new.txt"));
        Assert.Equal(2, server.Requests.Count);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-prior-clean: a normal run cleans the previous ledger before creating new outputs")]
    public async Task NormalRunCleansPriorLedgerBeforeCreatingOutputs()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Files(FileSetEntry.TextFile("old.txt", "old", alwaysOverwrite: true)),
            ToolBehavior.Files(FileSetEntry.TextFile("new.txt", "new", alwaysOverwrite: true)));

        var first = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);
        var second = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        AssertSuccess(first);
        AssertSuccess(second);
        var oldPath = CliReportedPath(Path.Combine(sandbox.ProjectPath, "old.txt"));
        var newPath = CliReportedPath(Path.Combine(sandbox.ProjectPath, "new.txt"));
        var cleanIndex = second.Stdout.IndexOf($"[cli] Cleaning {oldPath}", StringComparison.Ordinal);
        var createIndex = second.Stdout.IndexOf($"[cli] Creating {newPath}", StringComparison.Ordinal);
        Assert.True(cleanIndex >= 0, $"Missing clean line in:{Environment.NewLine}{second.Stdout}");
        Assert.True(createIndex > cleanIndex, $"Create line did not follow clean line in:{Environment.NewLine}{second.Stdout}");
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "old.txt")));
        Assert.Equal("new", sandbox.ReadFile("new.txt"));
        Assert.Equal(2, server.Requests.Count);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-logs-display: tool log levels are prefixed and debug logs require -debug")]
    public async Task ToolLogsArePrefixedAndDebugLogsRequireDebugFlag()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            LoggedSuccess(),
            LoggedSuccess());

        var normal = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);
        var debug = await cli.Run(
            ["echo", "-i", "in.txt", "-debug"],
            sandbox.ProjectPath,
            sandbox);

        AssertSuccess(normal);
        AssertSuccess(debug);
        foreach (var expected in new[]
                 {
                     "[echo v2026.01.01.0001] message text",
                     "[echo v2026.01.01.0001] info text",
                     "[echo v2026.01.01.0001] warning text",
                 })
        {
            Assert.Contains(expected, normal.Stdout, StringComparison.Ordinal);
            Assert.Contains(expected, debug.Stdout, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("debug text", normal.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            "[echo v2026.01.01.0001] debug text",
            debug.Stdout,
            StringComparison.Ordinal);
        Assert.Equal(2, server.Requests.Count);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-logs-error-promoted: an error-level tool log prevents output and fails the run")]
    public async Task ErrorLogIsPromotedToFailure()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Files(
                    FileSetEntry.TextFile("must-not-exist.txt", "bad", alwaysOverwrite: true))
                .WithLogs(new MockLog("error", "boom")));

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(result.Failed);
        Assert.Contains("*** TRANSPILER ERROR ***", result.Combined, StringComparison.Ordinal);
        Assert.Contains("[echo v2026.01.01.0001] boom", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("ERROR: boom", result.Combined, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "must-not-exist.txt")));
        Assert.Single(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-exception: response exceptions print outer, stack, and inner details and fail")]
    public async Task ResponseExceptionPrintsCompleteChainAndFails()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            new ExceptionChainBehavior(
                "outer failure",
                "inner failure",
                "at Mock.Transpiler()"));

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(result.Failed);
        Assert.Contains("*** TRANSPILER ERROR ***", result.Combined, StringComparison.Ordinal);
        Assert.Contains("ERROR: outer failure", result.Combined, StringComparison.Ordinal);
        Assert.Contains("at Mock.Transpiler()", result.Combined, StringComparison.Ordinal);
        Assert.Contains("ERROR: inner failure", result.Combined, StringComparison.Ordinal);
        Assert.Single(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-non-json: a non-JSON success response is surfaced as a transpiler failure")]
    public async Task NonJsonResponseIsSurfacedAsFailure()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue("echo", ToolBehavior.Text("oops"));

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(result.Failed);
        Assert.Contains("*** TRANSPILER ERROR ***", result.Combined, StringComparison.Ordinal);
        Assert.Contains(
            "Proxy server returned non-JSON response: oops",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Single(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-status-message: a plain-text transpiler status is reported as tool-not-found")]
    public async Task PlainTextStatusIsReportedAsToolNotFound()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue("echo", ToolBehavior.Text("False"));

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(result.Failed);
        Assert.Contains("ERROR: Tool '", result.Combined, StringComparison.Ordinal);
        Assert.Contains("' does not exist.", result.Combined, StringComparison.Ordinal);
        Assert.Contains(
            "The tool was not found locally or on the tools server.",
            result.Combined,
            StringComparison.Ordinal);
        Assert.DoesNotContain("*** TRANSPILER ERROR ***", result.Combined, StringComparison.Ordinal);
        Assert.Single(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-http-500: non-retryable HTTP failures surface status and body after one request")]
    public async Task Http500IsNotRetried()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Status(500, """{"error":"Internal server error"}"""));

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(result.Failed);
        Assert.Contains(
            """Proxy request failed with status InternalServerError: {"error":"Internal server error"}""",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Single(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-retry-503: two service-unavailable responses are logged and retried before success")]
    [Trait("Slow", "true")]
    public async Task ServiceUnavailableIsRetriedBeforeSuccess()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Status(503, """{"error":"booting-1"}"""),
            ToolBehavior.Status(503, """{"error":"booting-2"}"""),
            ToolBehavior.Files(
                FileSetEntry.TextFile("ready.txt", "ready", alwaysOverwrite: true)));

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 30_000);

        AssertSuccess(result);
        Assert.Contains(
            "[cli] [echo] Remote transpiler not ready (ServiceUnavailable). Retrying... (attempt 1/10)",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            "[cli] [echo] Remote transpiler not ready (ServiceUnavailable). Retrying... (attempt 2/10)",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Equal("ready", sandbox.ReadFile("ready.txt"));
        Assert.Equal(3, server.Requests.Count);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-retry-400-ssl: a 400 response with an SSL body is retried before success")]
    [Trait("Slow", "true")]
    public async Task BadRequestWithSslBodyIsRetriedBeforeSuccess()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Status(400, """{"error":"SSL connection could not be established"}"""),
            ToolBehavior.Files(
                FileSetEntry.TextFile("ready.txt", "ready", alwaysOverwrite: true)));

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 30_000);

        AssertSuccess(result);
        Assert.Contains(
            "[cli] [echo] Remote transpiler SSL error. Retrying in 6 seconds... (attempt 1/10)",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Equal("ready", sandbox.ReadFile("ready.txt"));
        Assert.Equal(2, server.Requests.Count);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-retry-host-not-found: unknown hosts log a retry then hit the requested wait bound")]
    [Trait("Slow", "true")]
    public async Task UnknownHostRetriesThenTimesOut()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        WriteToolUrls(
            sandbox,
            server,
            new KeyValuePair<string, string>("ghost", "http://nonexistent.invalid/"));

        var result = await cli.Run(
            ["ghost", "-i", "in.txt", "-w", "8000"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 20_000);

        Assert.True(result.Failed);
        Assert.Contains(
            "Host not found: nonexistent.invalid. Retrying in 6 seconds...",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Contains("Timed out waiting for cook", result.Combined, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-exception-ssl-hint: an SSL handshake exception is retried with an SSL-specific message")]
    [Trait("Slow", "true")]
    public async Task SslHandshakeExceptionIsRetriedWithSslSpecificMessage()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        WriteToolUrls(
            sandbox,
            server,
            new KeyValuePair<string, string>("insecure", $"https://127.0.0.1:{server.Port}/"));

        var result = await cli.Run(
            ["insecure", "-i", "in.txt", "-w", "8000"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 20_000);

        Assert.True(result.Failed);
        Assert.Contains(
            "SSL connection error. Retrying in 6 seconds... (attempt 1/10)",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Contains("Timed out waiting for cook", result.Combined, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-connection-refused: three refused connections stop retrying and eventually time out")]
    [Trait("Slow", "true")]
    public async Task ConnectionRefusedStopsAfterThreeAttempts()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        WriteToolUrls(
            sandbox,
            server,
            new KeyValuePair<string, string>("dead", "http://127.0.0.1:1/"));

        var result = await cli.Run(
            ["dead", "-i", "in.txt", "-w", "30000"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 45_000);

        Assert.True(result.Failed);
        Assert.Contains(
            "Connection error (ConnectionRefused). Retrying in 6 seconds... (attempt 1/10)",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Contains(
            "Connection error (ConnectionRefused). Retrying in 6 seconds... (attempt 2/10)",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Contains(
            "ERROR: Connection refused 3 times for: http://127.0.0.1:1/",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Contains("Timed out waiting for cook", result.Combined, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-timeout: -w is transmitted and bounds a delayed tool request")]
    public async Task WaitTimeoutIsTransmittedAndBoundsDelayedRequest()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Delay(5_000, SuccessFiles()));

        var result = await cli.Run(
            ["echo", "-i", "in.txt", "-w", "1500"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 10_000);
        var request = await server.WaitForRequestAsync();

        Assert.True(result.Failed);
        Assert.Equal(1_500, request.CliWaitTimeout);
        Assert.Contains("Timed out waiting for cook", result.Combined, StringComparison.Ordinal);
        Assert.InRange(result.Duration, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));
        await Task.Delay(4_000);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-boot-spinner: a slow cold-start shows the boot-up spinner then succeeds")]
    [Trait("Slow", "true")]
    public async Task SlowColdStartShowsBootSpinnerThenSucceeds()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Delay(17_000, SuccessFiles()));

        var result = await cli.Run(
            ["echo", "-i", "in.txt", "-w", "60000"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 40_000);

        AssertSuccess(result);
        Assert.Contains(
            "Waiting for the remote transpiler '",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("to boot up", result.Stdout, StringComparison.Ordinal);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-async-completed: pending async responses are polled until files are returned")]
    [Trait("Slow", "true")]
    public async Task PendingAsyncResponseIsPolledUntilCompleted()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Async(
                pendingPolls: 1,
                ToolBehavior.Files(
                    FileSetEntry.TextFile("async.txt", "complete", alwaysOverwrite: true))));

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 20_000);

        AssertSuccess(result);
        Assert.Contains(
            "[cli] Transpiler processing asynchronously, polling for result...",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Equal("complete", sandbox.ReadFile("async.txt"));
        Assert.Single(server.Requests);
        Assert.True(
            result.Duration >= TimeSpan.FromSeconds(5),
            $"Expected polling delay, actual duration was {result.Duration}.");
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-async-failed: a failed async terminal payload surfaces its exception")]
    [Trait("Slow", "true")]
    public async Task FailedAsyncResponseSurfacesException()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Async(
                pendingPolls: 0,
                new FailedAsyncTerminalBehavior("async exploded")));

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 15_000);

        Assert.True(result.Failed);
        Assert.Contains(
            "[cli] Transpiler processing asynchronously, polling for result...",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("*** TRANSPILER ERROR ***", result.Combined, StringComparison.Ordinal);
        Assert.Contains("ERROR: async exploded", result.Combined, StringComparison.Ordinal);
        Assert.Contains("async failure stack", result.Combined, StringComparison.Ordinal);
        Assert.Single(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-async-404: a 404 poll response reports the task as no longer found")]
    [Trait("Slow", "true")]
    public async Task AsyncPollNotFoundReportsTaskGone()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue("echo", ToolBehavior.AsyncNotFound());

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 15_000);

        Assert.True(result.Failed);
        Assert.Contains(
            "ERROR: Async task not found on transpiler (container may have restarted). Please retry.",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Single(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-async-timeout: an always-pending poll times out at the requested wait bound")]
    [Trait("Slow", "true")]
    public async Task AsyncPollAlwaysPendingTimesOutAtWaitBound()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Async(
                pendingPolls: int.MaxValue,
                ToolBehavior.Files(
                    FileSetEntry.TextFile("async.txt", "complete", alwaysOverwrite: true))));

        var result = await cli.Run(
            ["echo", "-i", "in.txt", "-w", "7000"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 15_000);

        Assert.True(result.Failed);
        Assert.Contains(
            "ERROR: Timed out waiting for async transpiler task to complete",
            result.Combined,
            StringComparison.Ordinal);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-label-direct: direct catalog runs print the resolved latest-version label first")]
    public async Task DirectCatalogRunPrintsResolvedLabelFirst()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "mixed Case");
        WriteToolUrls(sandbox, server);
        server.Enqueue("to-uppercase", ToolBehavior.Echo());

        var result = await cli.Run(
            ["to-uppercase", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        AssertSuccess(result);
        Assert.StartsWith(
            "cli:> effortless/common/to-uppercase v2026.01.01.0001 [latest]",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Equal("MIXED CASE", sandbox.ReadFile("Output.txt"));
        var request = Assert.Single(server.Requests);
        Assert.Equal("to-uppercase", request.ToolName);
        Assert.Equal("v2026.01.01.0001", request.Version);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-bare-url: bare tool URLs run without labels and include positional parameters")]
    public async Task BareUrlRunsWithoutLabelAndIncludesPositionalParameter()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        server.Enqueue(
            "echo",
            ToolBehavior.Files(
                FileSetEntry.TextFile("url-output.txt", "url", alwaysOverwrite: true)));
        var url = server.ToolUri("echo").ToString();

        var result = await cli.Run(
            [url, "extra1", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        AssertSuccess(result);
        Assert.DoesNotContain("cli:>", result.Stdout, StringComparison.Ordinal);
        var request = Assert.Single(server.Requests);
        Assert.Contains("param1=extra1", request.CliParams);
        Assert.Equal("echo", request.ToolName);
        Assert.Equal(SanitizeUrl(url), request.CliTranspiler);
        Assert.Equal("url", sandbox.ReadFile("url-output.txt"));
        var ledger = ParseLedger(LedgerPath(sandbox, server, "echo"));
        Assert.Equal("url-output.txt", Assert.Single(ledger).RelativePath);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-account-env: -account injects apiKey and baseId from effortless.env")]
    public async Task AccountParametersComeFromProjectEnvFile()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        sandbox.WriteFile("effortless.env", "ACME_PAT=p1\nACME_BASEID=app1\n");
        server.Enqueue("echo", SuccessFiles());

        var result = await cli.Run(
            ["echo", "-i", "in.txt", "-account", "acme"],
            sandbox.ProjectPath,
            sandbox);
        var request = await server.WaitForRequestAsync();

        AssertSuccess(result);
        Assert.Equal("acme", request.CliAccount);
        Assert.Contains("apiKey=p1", request.CliParams);
        Assert.Contains("baseId=app1", request.CliParams);
        var parameters = request.CliParams.ToArray();
        Assert.True(
            Array.IndexOf(parameters, "baseId=app1") > Array.IndexOf(parameters, "apiKey=p1"),
            "Expected account parameters in apiKey, baseId resolution order.");
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-account-keyfile: -a falls back to the account key in effortless.key")]
    public async Task AccountApiKeyFallsBackToDefaultKeyFile()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        WriteKeyFile(sandbox, "effortless.key", "acme", "k1");
        server.Enqueue("echo", SuccessFiles());

        var result = await cli.Run(
            ["echo", "-i", "in.txt", "-a", "acme"],
            sandbox.ProjectPath,
            sandbox);
        var request = await server.WaitForRequestAsync();

        AssertSuccess(result);
        Assert.Equal("acme", request.CliAccount);
        Assert.Contains("apiKey=k1", request.CliParams);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-account-json-key: legacy rejects JSON API keys but exits zero before POST")]
    public async Task JsonValuedAccountKeyIsRejectedBeforePost()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        WriteKeyFile(sandbox, "effortless.key", "baserow", """{"token":"complex"}""");

        var result = await cli.Run(
            ["echo", "-i", "in.txt", "-account", "baserow"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "The API key for account 'baserow' is in JSON format and cannot be used directly",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Contains("*** TRANSPILER ERROR ***", result.Combined, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-account-runas: -runAs selects effortless.<user>.key for account injection")]
    public async Task RunAsSelectsNamedKeyFile()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        WriteKeyFile(sandbox, "effortless.key", "acme", "default-key");
        WriteKeyFile(sandbox, "effortless.bob.key", "acme", "kb");
        server.Enqueue("echo", SuccessFiles());

        var result = await cli.Run(
            ["echo", "-i", "in.txt", "-a", "acme", "-runAs", "bob"],
            sandbox.ProjectPath,
            sandbox);
        var request = await server.WaitForRequestAsync();

        AssertSuccess(result);
        Assert.Contains("apiKey=kb", request.CliParams);
        Assert.DoesNotContain("apiKey=default-key", request.CliParams);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-jwt-project-first: project JWT wins, expired tokens pass through, then global JWT is used")]
    public async Task ProjectJwtTakesPrecedenceAndGlobalJwtIsFallback()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");
        const string expiredProjectJwt = "e30.eyJleHAiOjB9.signature";
        const string globalJwt = "global-token";
        sandbox.WriteFile("effortless.env", $"EFFORTLESS_JWT={expiredProjectJwt}\n");
        sandbox.WriteHomeFile(".effortless/effortlessapi_token.txt", globalJwt);
        server.Enqueue("echo", SuccessFiles(), SuccessFiles());

        var projectResult = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);
        var projectRequest = await server.WaitForRequestAsync();

        AssertSuccess(projectResult);
        Assert.Equal(expiredProjectJwt, projectRequest.CliJwt);

        sandbox.WriteFile("effortless.env", "# no project token\n");
        var globalResult = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);
        var globalRequest = await server.WaitForRequestAsync(ordinal: 2);

        AssertSuccess(globalResult);
        Assert.Equal(globalJwt, globalRequest.CliJwt);
        Assert.Equal(2, server.Requests.Count);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-outside-project: direct tool runs require a project and do not POST without one")]
    public async Task DirectToolRunOutsideProjectFailsBeforePost()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        server.IndexJson = index.Json;
        using var sandbox = Sandbox.Create(cli);
        sandbox.SeedHome(index);
        sandbox.WriteFile("in.txt", "input");

        var result = await cli.Run(
            ["echo", "-i", "in.txt"],
            sandbox.ProjectPath,
            sandbox);

        Assert.True(result.Failed);
        Assert.Contains("ERROR: No project found in this directory", result.Combined, StringComparison.Ordinal);
        Assert.Contains(
            "Run `effortless -init` to create a new project",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Empty(server.Requests);
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "tx-tool-not-found: legacy non-empty indexes skip refresh and end with the tool-not-found block")]
    [Trait("Slow", "true")]
    public async Task MissingToolInNonEmptyIndexSkipsRefreshAndEndsWithNotFoundBlock()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = CreateSandbox(cli, server);
        sandbox.WriteFile("in.txt", "input");

        var result = await cli.Run(
            ["nope", "-i", "in.txt", "-w", "8000"],
            sandbox.ProjectPath,
            sandbox,
            timeoutMs: 20_000);

        Assert.True(result.Failed);
        Assert.DoesNotContain("Refreshing CLI tool URL index...", result.Combined, StringComparison.Ordinal);
        Assert.DoesNotContain("CLOUD-BRIDGE CALL TRIGGERED:", result.Combined, StringComparison.Ordinal);
        Assert.Contains("ERROR: Tool 'nope' does not exist.", result.Combined, StringComparison.Ordinal);
        var expectedListCommand = "listTools";
        var unexpectedListCommand = "listToolUrls";
        Assert.Contains(
            $"To see available tools, run:{Environment.NewLine}  effortless {expectedListCommand}{Environment.NewLine}",
            result.Combined,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"  effortless {unexpectedListCommand}{Environment.NewLine}",
            result.Combined,
            StringComparison.Ordinal);
        Assert.Empty(server.Requests);
        server.ThrowIfFaulted();
    }

    private static Sandbox CreateSandbox(CliUnderTest cli, MockToolServer server)
    {
        var index = IndexFixture.Load(server);
        server.IndexJson = index.Json;
        var sandbox = Sandbox.Create(cli);
        sandbox.SeedHome(index);
        sandbox.SeedProject("transpile");
        return sandbox;
    }

    private static ToolBehavior SuccessFiles() =>
        ToolBehavior.Files(FileSetEntry.TextFile("success.txt", "success", alwaysOverwrite: true));

    private static ToolBehavior LoggedSuccess() =>
        ToolBehavior.Files(
                FileSetEntry.TextFile("logged.txt", "logged", alwaysOverwrite: true))
            .WithLogs(
                new MockLog("message", "message text"),
                new MockLog("info", "info text"),
                new MockLog("warning", "warning text"),
                new MockLog("debug", "debug text"));

    private static void WriteToolUrls(
        Sandbox sandbox,
        MockToolServer server,
        params KeyValuePair<string, string>[] additionalUrls)
    {
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cli-cloud-bridge"] = server.BridgeUri.ToString(),
        };
        foreach (var pair in additionalUrls)
        {
            urls.Add(pair.Key, pair.Value);
        }

        sandbox.WriteHomeFile(
            ".effortless/tool_urls.json",
            JsonSerializer.Serialize(urls, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteKeyFile(
        Sandbox sandbox,
        string fileName,
        string account,
        string value)
    {
        sandbox.WriteHomeFile(
            $".effortless/{fileName}",
            JsonSerializer.Serialize(
                new
                {
                    EmailAddress = "test@example.invalid",
                    Secret = "not-used",
                    APIKeys = new Dictionary<string, string>
                    {
                        [account] = value,
                    },
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ProjectJson(string name) =>
        JsonSerializer.Serialize(
            new
            {
                Name = name,
                SSoTmeProjectId = Guid.NewGuid(),
                ProjectSettings = new[]
                {
                    new { Name = "project-name", Value = name },
                },
                ProjectTranspilers = Array.Empty<object>(),
            },
            new JsonSerializerOptions { WriteIndented = true });

    private static string LedgerPath(Sandbox sandbox, MockToolServer server, string toolName) =>
        Path.Combine(
            sandbox.ProjectPath,
            ".effortless",
            SanitizeUrl(server.ToolUri(toolName).ToString()) + ".zfs");

    private static IReadOnlyList<FileSetEntry> ParseLedger(string path) =>
        FileSetXml.Parse(Encoding.UTF8.GetString(FileSetXml.Gunzip(File.ReadAllBytes(path))));

    private static string CliReportedPath(string path) =>
        OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal)
            ? "/private" + path
            : path;

    private static string SanitizeUrl(string value) =>
        value
            .Replace("/", string.Empty)
            .Replace(".", string.Empty)
            .Replace(":", string.Empty)
            .Replace("?", string.Empty)
            .Replace("&", string.Empty)
            .Replace("=", string.Empty)
            .Replace(" ", "-")
            .ToLowerInvariant();

    private static void AssertSuccess(CliResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
    }
}
