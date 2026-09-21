using System.Security.Cryptography;
using System.Text.Json;
using Effortless.Cli.E2E.Harness;

namespace Effortless.Cli.E2E.Suites;

[CollectionDefinition(ShimTestCollection.Name, DisableParallelization = true)]
public sealed class ShimTestCollection
{
    public const string Name = "e2e-shim serial";
}

[Collection(ShimTestCollection.Name)]
public sealed class ShimTests
{
    [Fact(DisplayName = "shim-argv-passthrough: cli.js passes arguments")]
    public async Task CliJsPinsLegacyArgumentJoiningAndVersionPassthrough()
    {
        var cli = new CliUnderTest();
        await using var server = new MockToolServer();
        var index = IndexFixture.Load(server);
        server.IndexJson = index.Json;
        server.Enqueue("echo", ToolBehavior.Echo());
        using var sandbox = Sandbox.Create(cli);
        sandbox.SeedHome(index);
        sandbox.SeedProject("transpile");
        sandbox.WriteFile("in.txt", "shim input");
        var shim = ShimUnderTest.CreatePrebuilt(cli, sandbox);
        var csprojBefore = File.ReadAllText(shim.CsprojPath);
        var versionConstantBefore =
            File.ReadAllText(shim.VersionConstantPath);

        var version = await shim.RunNode(["-version"], sandbox.ProjectPath);
        var transpile = await shim.RunNode(
            ["echo", "-i", "in.txt", "-p", "a=b c"],
            sandbox.ProjectPath);
        var request = await server.WaitForRequestAsync();

        Assert.Equal(0, version.ExitCode);
        Assert.Equal(
            CliUnderTest.DisplayVersion + Environment.NewLine,
            version.Stdout);
        Assert.Equal(0, transpile.ExitCode);
        Assert.Contains("a=b c", request.CliParams);
        Assert.DoesNotContain("param1=c", request.CliParams);
        

        Assert.Equal(csprojBefore, File.ReadAllText(shim.CsprojPath));
        Assert.Equal(
            versionConstantBefore,
            File.ReadAllText(shim.VersionConstantPath));
        server.ThrowIfFaulted();
    }

    [Fact(DisplayName = "shim-exit-code: cli.js propagates the exit code")]
    public async Task CliJsPinsLegacyIgnoredChildExitCode()
    {
        var cli = new CliUnderTest();
        using var sandbox = Sandbox.Create(cli);
        var shim = ShimUnderTest.CreatePrebuilt(cli, sandbox);

        var result = await shim.RunNode(["-describe"], sandbox.ProjectPath);

        Assert.Contains(
            "ERROR: No project found in this directory or any parent directory.",
            result.Combined,
            StringComparison.Ordinal);
        Assert.True(result.Failed);
        
    }

    [Fact(DisplayName = "shim-version-sync: cli.js syncs the version")]
    [Trait("Slow", "true")]
    public async Task CliJsSynchronizesVersionInAnIsolatedSourceTree()
    {
        const string testVersion = "2099.1231.2359";
        const string expectedCsprojVersion = "2099.12.31.2359";
        const string expectedDisplayVersion = "v2099-12-31-2359";
        var cli = new CliUnderTest();
        using var sandbox = Sandbox.Create(cli);
        var sourcePaths = VersionSourcePaths(cli);
        var sourceHashesBefore = sourcePaths.ToDictionary(path => path, HashFile);
        var shim = ShimUnderTest.CreateFullSource(cli, sandbox);
        shim.SetPackageVersion(testVersion);

        var result = await shim.RunNode(
            ["-version"],
            sandbox.ProjectPath,
            timeoutMs: 300_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("developer mode", result.Combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedDisplayVersion + Environment.NewLine, result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            $"<Version>{expectedCsprojVersion}</Version>",
            File.ReadAllText(shim.CsprojPath),
            StringComparison.Ordinal);
        Assert.Contains(
            $"public const string Value = \"{testVersion}\";",
            File.ReadAllText(shim.VersionConstantPath),
            StringComparison.Ordinal);
        Assert.Contains(
            $"public const string DisplayVersion = \"{expectedDisplayVersion}\";",
            File.ReadAllText(shim.VersionConstantPath),
            StringComparison.Ordinal);
        foreach (var path in sourcePaths)
        {
            Assert.Equal(sourceHashesBefore[path], HashFile(path));
        }
    }

    [Fact(DisplayName = "shim-stale-dll-rebuild: a synced-but-unstamped tree rebuilds once, then stays built")]
    public async Task CliJsRebuildsAStaleDllEvenWhenVersionSourcesAreAlreadySynced()
    {
        // Reproduces the real incident: a `git pull` lands a release commit
        // whose .csproj/CliVersion.cs are already stamped to the new version
        // (release.sh commits them pre-synced), sitting next to a DLL that
        // was compiled for an older version. syncVersionFromPackageJson()'s
        // own "did I have to edit a file" signal sees nothing to change and
        // must not be the only rebuild trigger, or the stale DLL runs
        // forever with no error and no visible change in -version.
        var cli = new CliUnderTest();
        using var sandbox = Sandbox.Create(cli);
        var shim = ShimUnderTest.CreateFullSource(cli, sandbox);
        var outputDestination = Path.Combine(
            shim.RootPath,
            "src",
            "Effortless.Cli",
            "bin",
            "Release",
            "net8.0");
        Directory.CreateDirectory(outputDestination);
        var outputSource = Path.GetDirectoryName(cli.DllPath)
            ?? throw new InvalidOperationException("The CLI DLL has no containing directory.");
        foreach (var file in Directory.GetFiles(outputSource))
        {
            File.Copy(file, Path.Combine(outputDestination, Path.GetFileName(file)), overwrite: true);
        }

        var stampPath = Path.Combine(outputDestination, ".built-version");
        File.Delete(stampPath);

        var firstRun = await shim.RunNode(
            ["-version"],
            sandbox.ProjectPath,
            timeoutMs: 300_000);

        Assert.Equal(0, firstRun.ExitCode);
        Assert.Contains("developer mode", firstRun.Combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CliUnderTest.DisplayVersion + Environment.NewLine, firstRun.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(stampPath));
        Assert.Equal(CliUnderTest.PackageVersion, File.ReadAllText(stampPath).Trim());

        var secondRun = await shim.RunNode(["-version"], sandbox.ProjectPath);

        Assert.Equal(0, secondRun.ExitCode);
        Assert.DoesNotContain("developer mode", secondRun.Combined, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            CliUnderTest.DisplayVersion + Environment.NewLine,
            secondRun.Stdout);
    }

    [Fact(DisplayName = "shim-package-identity: scoped package owns every alias")]
    public void ScopedPackageOwnsEveryAlias()
    {
        var cli = new CliUnderTest();
        using var package = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(CliUnderTest.Root, "package.json")));

        Assert.Equal(
            "@effortlessapi/cli",
            package.RootElement.GetProperty("name").GetString());
        Assert.Equal(
            new[] { "aic", "aicapture", "effortless", "ssotme" },
            package.RootElement
                .GetProperty("bin")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.All(
            package.RootElement.GetProperty("bin").EnumerateObject(),
            property => Assert.Equal("cli.js", property.Value.GetString()));
    }

    [Fact(DisplayName = "shim-aliases: all four bin names work")]
    public async Task AllNpmAliasesInvokeTheSameShim()
    {
        var cli = new CliUnderTest();
        using var sandbox = Sandbox.Create(cli);
        var shim = ShimUnderTest.CreatePrebuilt(cli, sandbox);
        var aliases = shim.PackageBinNames();
        Assert.Equal(
            new[] { "aic", "aicapture", "effortless", "ssotme" },
            aliases);
        await shim.InstallAliases();

        var outputs = new List<string>();
        foreach (var alias in aliases)
        {
            var result = await shim.RunAlias(alias, ["-v"], sandbox.ProjectPath);
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Stderr);
            outputs.Add(result.Stdout);
        }

        Assert.All(
            outputs,
            output => Assert.Equal(CliUnderTest.DisplayVersion + Environment.NewLine, output));
        Assert.Single(outputs.Distinct(StringComparer.Ordinal));
    }

    private static IReadOnlyList<string> VersionSourcePaths(
        CliUnderTest cli)
    {
        var paths = new List<string>
        {
            Path.Combine(CliUnderTest.Root, "package.json"),
            Path.Combine(CliUnderTest.Root, "cli.js"),
        };
        paths.Add(
            Path.Combine(
                CliUnderTest.Root,
                "src",
                "Effortless.Cli",
                "Effortless.Cli.csproj"));
        paths.Add(
            Path.Combine(
                CliUnderTest.Root,
                "src",
                "Effortless.Cli.Core",
                "CliVersion.cs"));
        

        return paths;
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
