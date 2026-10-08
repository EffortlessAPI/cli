using Effortless.Cli.Project;

namespace Effortless.Cli.Tests;

public sealed class ProjectModelTests
{
    [Fact(DisplayName = "unit-tool-name-matches: tool names match legacy short and qualified forms")]
    public void ToolNamesMatchLegacyShortAndQualifiedForms()
    {
        Assert.True(EffortlessProject.ToolNameMatches("to-uppercase", "TO-UPPERCASE"));
        Assert.True(EffortlessProject.ToolNameMatches("to-uppercase", "common/to-uppercase"));
        Assert.True(EffortlessProject.ToolNameMatches(
            "effortless/common/to-uppercase",
            "to-uppercase"));
        Assert.True(EffortlessProject.ToolNameMatches(
            "effortless/common/to-uppercase/v2026.01.01.0001",
            "effortless/common/to-uppercase/v2026.01.01.0001"));
        Assert.False(EffortlessProject.ToolNameMatches(
            "effortless/common/to-uppercase/v2026.01.01.0001",
            "to-uppercase"));
        Assert.False(EffortlessProject.ToolNameMatches("to-lowercase", "to-uppercase"));
        Assert.True(EffortlessProject.VersionedToolNameMatches(
            "effortless/common/to-uppercase/v2026.01.01.0001",
            "to-uppercase"));
        Assert.True(EffortlessProject.VersionedToolNameMatches(
            "effortless/common/to-uppercase/v2026.01.01.0001",
            "effortless/common/to-uppercase"));
        Assert.False(EffortlessProject.VersionedToolNameMatches(
            "effortless/common/to-lowercase/v2026.01.01.0001",
            "to-uppercase"));
        Assert.Equal(
            "effortless/common/to-uppercase",
            EffortlessProject.GetToolName("effortless/common/to-uppercase -i input.txt"));
    }

    [Fact(DisplayName = "unit-is-at-path: ProjectTranspiler.IsAtPath matches legacy path rules")]
    public void IsAtPathMatchesLegacyPathRules()
    {
        var root = new ProjectTranspiler { RelativePath = string.Empty };
        var nested = new ProjectTranspiler { RelativePath = "/Sub/Child/" };

        Assert.True(root.IsAtPath(string.Empty));
        Assert.True(root.IsAtPath(string.Empty, exactMatch: true));
        Assert.False(root.IsAtPath("sub"));
        Assert.True(nested.IsAtPath(string.Empty));
        Assert.True(nested.IsAtPath(@"SUB\CHILD", exactMatch: true));
        Assert.True(nested.IsAtPath("sub"));
        Assert.True(nested.IsAtPath("s"));
        Assert.False(nested.IsAtPath("sub/child/grandchild"));
    }

    [Fact(DisplayName = "unit-sync-commandline-version: embedded versions follow the hard pin")]
    public void EmbeddedCommandLineVersionsFollowTheHardPin()
    {
        var withoutPin = new ProjectTranspiler
        {
            CommandLine = "common/echo/v2026.01.01.0001 -i input.txt",
        };
        var withoutVersion = new ProjectTranspiler
        {
            PinnedVersion = "v2026.02.02.0002",
            CommandLine = "echo -i input.txt",
        };
        var matching = new ProjectTranspiler
        {
            PinnedVersion = "v2026.02.02.0002",
            CommandLine = "common/echo/v2026.02.02.0002 -i input.txt",
        };
        var stale = new ProjectTranspiler
        {
            PinnedVersion = "v2026.02.02.0002",
            CommandLine = "common/echo/v2026.01.01.0001 -i input.txt",
        };

        Assert.False(withoutPin.SyncCommandLineVersion());
        Assert.False(withoutVersion.SyncCommandLineVersion());
        Assert.False(matching.SyncCommandLineVersion());
        Assert.True(stale.SyncCommandLineVersion());
        Assert.Equal(
            "common/echo/v2026.02.02.0002 -i input.txt",
            stale.CommandLine);
    }

    [Theory(DisplayName = "unit-commandline-capture: command line prefixes are stripped")]
    [InlineData("/tmp/ssotme.exe install echo -i a.txt", "echo -i a.txt")]
    [InlineData("/tmp/effortless.exe -install echo -i a.txt", "echo -i a.txt")]
    [InlineData("/tmp/aicapture.exe install echo", "echo")]
    [InlineData("/tmp/aic.exe install echo", "echo")]
    [InlineData("dotnet /tmp/SSoTme.OST.CLI.dll -install echo", "echo")]
    [InlineData("dotnet /tmp/AICapture.OST.CLI.dll install echo", "echo")]
    [InlineData("dotnet /tmp/Effortless.Cli.dll install echo", "echo")]
    [InlineData("/usr/local/bin/effortless install echo", "echo")]
    [InlineData("effortless install echo", "echo")]
    [InlineData("ssotme echo -p x=1", "echo -p x=1")]
    [InlineData("/home/u/.nvm/versions/node/v20/lib/node_modules/@effortlessapi/cli/node_modules/@effortlessapi/cli-linux-x64/bin/Effortless.Cli -install rulebook-to-postgres -i ../effortless-rulebook/effortless-rulebook.json", "rulebook-to-postgres -i ../effortless-rulebook/effortless-rulebook.json")]
    [InlineData("/opt/homebrew/lib/node_modules/@effortlessapi/cli/bin/Effortless.Cli rulebook-to-postgres -i r.json -install", "rulebook-to-postgres -i r.json")]
    [InlineData("\"C:\\Users\\u\\AppData\\Roaming\\npm\\node_modules\\@effortlessapi\\cli-win32-x64\\bin\\Effortless.Cli.exe\" -install echo -i a.txt", "echo -i a.txt")]
    public void CommandLinePrefixesAreStripped(string commandLine, string expected)
    {
        Assert.Equal(expected, ProjectTranspiler.CaptureCommandLine(commandLine));
    }
}
