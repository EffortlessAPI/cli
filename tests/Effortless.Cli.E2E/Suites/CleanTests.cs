using Effortless.Cli.E2E.Harness;

namespace Effortless.Cli.E2E.Suites;

public sealed class CleanTests
{
    [Theory(DisplayName = "clean-basic: clean forms honor overwrite and SkipClean ledger rules")]
    [InlineData("clean")]
    [InlineData("-clean")]
    [InlineData("-c")]
    public async Task CleanHonorsLedgerRules(string form)
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep("Cleaner", "", "to-uppercase"));
        server.Enqueue(
            "to-uppercase",
            ToolBehavior.Files(
                FileSetEntry.TextFile("out.txt", "remove", alwaysOverwrite: true),
                FileSetEntry.TextFile("untouched.txt", "scaffold", overwriteMode: "Never"),
                FileSetEntry.TextFile("keep.txt", "keep", overwriteMode: "Never"),
                FileSetEntry.TextFile("skip.txt", "skip", alwaysOverwrite: true, skipClean: true),
                FileSetEntry.BinaryFile(
                    "empty/bin.dat",
                    [0, 1, 2, 255],
                    alwaysOverwrite: true,
                    zipped: true)));
        var build = await cli.Run(["build"], sandbox.ProjectPath, sandbox);
        Assert.Equal(0, build.ExitCode);
        Assert.Single(WorkflowTestSupport.ZfsFiles(sandbox));

        // A hand-edit is what makes a Never file the user's; an unedited one is still generated output.
        sandbox.WriteFile("keep.txt", "keep, hand edited");

        var result = await cli.Run([form], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[cli] Cleaning", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("out.txt", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("bin.dat", result.Stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "out.txt")));
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "empty", "bin.dat")));
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "untouched.txt")));
        Assert.False(Directory.Exists(Path.Combine(sandbox.ProjectPath, "empty")));
        Assert.Equal("keep, hand edited", sandbox.ReadFile("keep.txt"));
        Assert.Equal("skip", sandbox.ReadFile("skip.txt"));
        Assert.Empty(WorkflowTestSupport.ZfsFiles(sandbox));
        Assert.True(Directory.Exists(sandbox.ProjectPath));
    }

    [Theory(DisplayName = "clean-preserve-zfs: preserveZFS aliases retain ledgers after cleaning")]
    [InlineData("-preserveZFS")]
    [InlineData("-rz")]
    public async Task PreserveZfsRetainsLedger(string option)
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep("Cleaner", "", "to-uppercase"));
        server.Enqueue(
            "to-uppercase",
            ToolBehavior.Files(
                FileSetEntry.TextFile("out.txt", "remove", alwaysOverwrite: true)));
        var build = await cli.Run(["build"], sandbox.ProjectPath, sandbox);
        Assert.Equal(0, build.ExitCode);
        var ledger = Assert.Single(WorkflowTestSupport.ZfsFiles(sandbox));

        var result = await cli.Run(
            ["clean", option],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "out.txt")));
        Assert.True(File.Exists(ledger));
    }

    [Fact(DisplayName = "clean-local: cleanLocal cleans only the exact current path")]
    public async Task CleanLocalCleansOnlyExactPath()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep("Root", "", "to-uppercase"),
            new WorkflowStep("Sub", "/sub", "echo"));
        Directory.CreateDirectory(Path.Combine(sandbox.ProjectPath, "sub"));
        server.Enqueue(
            "to-uppercase",
            ToolBehavior.Files(
                FileSetEntry.TextFile("root.txt", "root", alwaysOverwrite: true)));
        server.Enqueue(
            "echo",
            ToolBehavior.Files(
                FileSetEntry.TextFile("sub.txt", "sub", alwaysOverwrite: true)));
        var build = await cli.Run(["build"], sandbox.ProjectPath, sandbox);
        Assert.Equal(0, build.ExitCode);

        var result = await cli.Run(
            ["cleanlocal"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "root.txt")));
        Assert.True(File.Exists(Path.Combine(sandbox.ProjectPath, "sub", "sub.txt")));
        Assert.Empty(WorkflowTestSupport.ZfsFiles(sandbox));
        Assert.Single(WorkflowTestSupport.ZfsFiles(sandbox, "sub"));
    }

    [Fact(DisplayName = "clean-subtree: clean from a subdirectory cleans only that subtree")]
    public async Task CleanFromSubdirectoryCleansOnlySubtree()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep("Root", "", "to-uppercase"),
            new WorkflowStep("Sub", "/sub", "echo"));
        var sub = Directory.CreateDirectory(Path.Combine(sandbox.ProjectPath, "sub")).FullName;
        server.Enqueue(
            "to-uppercase",
            ToolBehavior.Files(
                FileSetEntry.TextFile("root.txt", "root", alwaysOverwrite: true)));
        server.Enqueue(
            "echo",
            ToolBehavior.Files(
                FileSetEntry.TextFile("sub.txt", "sub", alwaysOverwrite: true)));
        var build = await cli.Run(["build"], sandbox.ProjectPath, sandbox);
        Assert.Equal(0, build.ExitCode);

        var result = await cli.Run(["clean"], sub, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(sandbox.ProjectPath, "root.txt")));
        Assert.False(File.Exists(Path.Combine(sub, "sub.txt")));
        Assert.Single(WorkflowTestSupport.ZfsFiles(sandbox));
        Assert.Empty(WorkflowTestSupport.ZfsFiles(sandbox, "sub"));
    }

    [Fact(DisplayName = "clean-deleted-cwd: clean survives its own cwd being pruned away")]
    public async Task CleanSurvivesItsOwnCwdBeingPrunedAway()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep("Sub", "/sub", "echo"));
        var sub = Directory.CreateDirectory(Path.Combine(sandbox.ProjectPath, "sub")).FullName;
        server.Enqueue(
            "echo",
            ToolBehavior.Files(
                FileSetEntry.TextFile("sub.txt", "sub", alwaysOverwrite: true)));
        var build = await cli.Run(["build"], sandbox.ProjectPath, sandbox);
        Assert.Equal(0, build.ExitCode);

        var result = await cli.Run(["clean"], sub, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(sub, "sub.txt")));
        Assert.False(Directory.Exists(sub));
    }

    [Fact(DisplayName = "clean-all: cleanAll cleans the whole project from the root")]
    public async Task CleanAllCleansTheWholeProjectFromTheRoot()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep("Root", "", "to-uppercase"),
            new WorkflowStep("Sub", "/sub", "echo"));
        var sub = Directory.CreateDirectory(
            Path.Combine(sandbox.ProjectPath, "sub")).FullName;
        var nested = Path.Combine(sub, "nested");
        WorkflowTestSupport.WriteProjectAt(
            nested,
            new WorkflowStep("Nested", "", "echo"));
        server.Enqueue(
            "to-uppercase",
            ToolBehavior.Files(
                FileSetEntry.TextFile("root.txt", "root", alwaysOverwrite: true)));
        server.Enqueue(
            "echo",
            ToolBehavior.Files(
                FileSetEntry.TextFile("sub.txt", "sub", alwaysOverwrite: true)));
        server.Enqueue(
            "echo",
            ToolBehavior.Files(
                FileSetEntry.TextFile("nested.txt", "nested", alwaysOverwrite: true)));
        Assert.Equal(
            0,
            (await cli.Run(["buildAll"], sandbox.ProjectPath, sandbox)).ExitCode);
        Assert.Equal(0, (await cli.Run(["build"], nested, sandbox)).ExitCode);

        // Run from /sub: cleanAll behaves as if run from the root.
        var result = await cli.Run(["cleanall"], sub, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "root.txt")));
        Assert.False(File.Exists(Path.Combine(sub, "sub.txt")));

        // D6/D12: the nested project is a separate project and is left alone.
        Assert.True(File.Exists(Path.Combine(nested, "nested.txt")));
        Assert.DoesNotContain(
            "Executing 'effortless -clean' in ",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Theory(DisplayName = "clean-with-subprojects: cleanWithSubprojects cleans nested projects too")]
    [Trait("Slow", "true")]
    [InlineData("cleanWithSubprojects")]
    [InlineData("-cws")]
    public async Task CleanWithSubprojectsCleansNestedProjects(string form)
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep("Root", "", "to-uppercase"));
        var sub = Directory.CreateDirectory(
            Path.Combine(sandbox.ProjectPath, "sub")).FullName;
        var nested = Path.Combine(sub, "nested");
        WorkflowTestSupport.WriteProjectAt(
            nested,
            new WorkflowStep("Nested", "", "echo"));
        server.Enqueue(
            "to-uppercase",
            ToolBehavior.Files(
                FileSetEntry.TextFile("root.txt", "root", alwaysOverwrite: true)));
        server.Enqueue(
            "echo",
            ToolBehavior.Files(
                FileSetEntry.TextFile("nested.txt", "nested", alwaysOverwrite: true)));
        Assert.Equal(
            0,
            (await cli.Run(["build"], sandbox.ProjectPath, sandbox)).ExitCode);
        Assert.Equal(0, (await cli.Run(["build"], nested, sandbox)).ExitCode);

        var result = await cli.Run(
            [form],
            sub,
            sandbox,
            timeoutMs: 180_000);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "root.txt")));
        Assert.False(File.Exists(Path.Combine(nested, "nested.txt")));
        Assert.Contains(
            "Executing 'effortless -clean' in ",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("/sub/nested", result.Stdout, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "clean-purge: purge cleans and removes orphaned ledgers")]
    public async Task PurgeCleansOrphanedLedger()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep("Orphan", "", "to-uppercase"));
        server.Enqueue(
            "to-uppercase",
            ToolBehavior.Files(
                FileSetEntry.TextFile("orphan.txt", "orphan", alwaysOverwrite: true)));
        var build = await cli.Run(["build"], sandbox.ProjectPath, sandbox);
        Assert.Equal(0, build.ExitCode);
        var ledger = Assert.Single(WorkflowTestSupport.ZfsFiles(sandbox));
        WorkflowTestSupport.WriteProject(sandbox);

        var ordinary = await cli.Run(["clean"], sandbox.ProjectPath, sandbox);
        Assert.Equal(0, ordinary.ExitCode);
        Assert.True(File.Exists(Path.Combine(sandbox.ProjectPath, "orphan.txt")));
        Assert.True(File.Exists(ledger));

        var purged = await cli.Run(
            ["clean", "-purge"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, purged.ExitCode);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "orphan.txt")));
        Assert.False(File.Exists(ledger));
    }

    [Fact(DisplayName = "clean-zfs-key-lasturl: clean locates the ledger using LastUrl")]
    public async Task CleanUsesLastUrlForLedgerKey()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var targetUrl = server.ToolUri("to-uppercase").ToString();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep(
                "Remote",
                "",
                $"to-uppercase -g {targetUrl}",
                LastUrl: targetUrl));
        server.Enqueue(
            "to-uppercase",
            ToolBehavior.Files(
                FileSetEntry.TextFile("remote.txt", "remote", alwaysOverwrite: true)));
        var build = await cli.Run(["build"], sandbox.ProjectPath, sandbox);
        Assert.Equal(0, build.ExitCode);
        var ledger = Assert.Single(WorkflowTestSupport.ZfsFiles(sandbox));
        Assert.Contains(
            WorkflowTestSupport.SanitizeUrl(targetUrl),
            Path.GetFileNameWithoutExtension(ledger),
            StringComparison.Ordinal);
        WorkflowTestSupport.WriteProject(
            sandbox,
            new WorkflowStep(
                "DifferentName",
                "",
                "echo",
                LastUrl: targetUrl));

        var result = await cli.Run(["clean"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "remote.txt")));
        Assert.False(File.Exists(ledger));
        Assert.Single(server.Requests);
    }

    [Fact(DisplayName = "clean-zfs-fallback-single: clean falls back to the only ledger present")]
    public async Task CleanFallsBackToTheOnlyLedgerPresent()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep("Original", "", "to-uppercase"));
        server.Enqueue(
            "to-uppercase",
            ToolBehavior.Files(
                FileSetEntry.TextFile("out.txt", "value", alwaysOverwrite: true)));
        var build = await cli.Run(["build"], sandbox.ProjectPath, sandbox);
        Assert.Equal(0, build.ExitCode);
        var ledger = Assert.Single(WorkflowTestSupport.ZfsFiles(sandbox));
        var renamed = Path.Combine(Path.GetDirectoryName(ledger)!, "unexpected-name.zfs");
        File.Move(ledger, renamed);

        var result = await cli.Run(["clean", "-debug"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("DEBUG: Expected ZFS", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            $"not found, using '",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            "unexpected-name.zfs",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "out.txt")));
        Assert.False(File.Exists(renamed));
    }

    [Fact(DisplayName = "clean-zfs-input: -clean -i cleans a ledger file passed directly")]
    public async Task CleanWithInputLedgerCleansThatLedgerDirectly()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep("Original", "", "to-uppercase"));
        server.Enqueue(
            "to-uppercase",
            ToolBehavior.Files(
                FileSetEntry.TextFile("out.txt", "value", alwaysOverwrite: true)));
        var build = await cli.Run(["build"], sandbox.ProjectPath, sandbox);
        Assert.Equal(0, build.ExitCode);
        var ledger = Assert.Single(WorkflowTestSupport.ZfsFiles(sandbox));
        File.Copy(ledger, Path.Combine(sandbox.ProjectPath, "copy.zfs"));

        var result = await cli.Run(
            ["-clean", "-i", "copy.zfs"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "out.txt")));
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "copy.zfs")));
        Assert.True(File.Exists(ledger));
    }

    [Fact(DisplayName = "clean-tool-flag: direct tool -clean cleans instead of writing")]
    public async Task ToolCleanFlagUsesPriorLedgerWithoutPosting()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        using var sandbox = WorkflowTestSupport.CreateProjectSandbox(
            cli,
            server,
            new WorkflowStep("Direct", "", "to-uppercase -i in.txt"));
        sandbox.WriteFile("in.txt", "source");
        server.Enqueue(
            "to-uppercase",
            ToolBehavior.Files(
                FileSetEntry.TextFile("direct.txt", "direct", alwaysOverwrite: true)),
            ToolBehavior.Files(
                FileSetEntry.TextFile("rerun.txt", "must not be written", alwaysOverwrite: true)));
        var build = await cli.Run(["build"], sandbox.ProjectPath, sandbox);
        Assert.Equal(0, build.ExitCode);
        var ledger = Assert.Single(WorkflowTestSupport.ZfsFiles(sandbox));
        var ledgerContents = File.ReadAllBytes(ledger);

        var result = await cli.Run(
            ["to-uppercase", "-i", "in.txt", "-clean"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "direct.txt")));
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectPath, "rerun.txt")));
        Assert.True(File.Exists(ledger));
        Assert.Equal(ledgerContents, File.ReadAllBytes(ledger));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact(DisplayName = "clean-project-null: clean outside a project fails clearly")]
    public async Task CleanOutsideProjectFails()
    {
        var cli = new CliUnderTest();
        using var sandbox = Sandbox.Create(cli);

        var result = await cli.Run(["clean"], sandbox.ProjectPath, sandbox);

        Assert.True(result.Failed);
        Assert.Contains(
            "No Project found in this directory",
            result.Combined,
            StringComparison.OrdinalIgnoreCase);
    }
}
