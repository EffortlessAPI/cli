using Effortless.Cli.Commands;

namespace Effortless.Cli.Tests;

/// <summary>
/// With no file named, the save watchers look for the rulebook in a fixed
/// order: the current folder, the project root, then effortless-rulebook/, and
/// effortless-rulebook.json before rulebook.json in each.
/// </summary>
public sealed class SaveWatchRulebookSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory(
        "onsave-search-").FullName;

    private string Sub => Directory.CreateDirectory(
        Path.Combine(_root, "sub")).FullName;

    private string RulebookFolder => Directory.CreateDirectory(
        Path.Combine(_root, "effortless-rulebook")).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Touch(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "{}");
        return path;
    }

    private string? Find(string currentDirectory) =>
        BuildCommand.FindRulebookToWatch(currentDirectory, _root)?.FullName;

    [Fact(DisplayName = "unit-onsave-rulebook-search: the current folder wins, effortless-rulebook.json first")]
    public void CurrentFolderWins()
    {
        var sub = Sub;
        Touch(_root, "effortless-rulebook.json");
        Touch(RulebookFolder, "effortless-rulebook.json");
        Touch(sub, "rulebook.json");
        var expected = Touch(sub, "effortless-rulebook.json");

        Assert.Equal(expected, Find(sub));
    }

    [Fact(DisplayName = "unit-onsave-rulebook-search: rulebook.json in the current folder beats the root")]
    public void PlainRulebookInCurrentFolderBeatsTheRoot()
    {
        var sub = Sub;
        Touch(_root, "effortless-rulebook.json");
        var expected = Touch(sub, "rulebook.json");

        Assert.Equal(expected, Find(sub));
    }

    [Fact(DisplayName = "unit-onsave-rulebook-search: the project root comes before effortless-rulebook/")]
    public void RootBeforeRulebookFolder()
    {
        var sub = Sub;
        Touch(RulebookFolder, "effortless-rulebook.json");
        var expected = Touch(_root, "rulebook.json");

        Assert.Equal(expected, Find(sub));
    }

    [Fact(DisplayName = "unit-onsave-rulebook-search: effortless-rulebook/ is the last place looked")]
    public void RulebookFolderIsLast()
    {
        var expected = Touch(RulebookFolder, "effortless-rulebook.json");

        Assert.Equal(expected, Find(Sub));
    }

    [Fact(DisplayName = "unit-onsave-rulebook-search: nothing found is null, and every place is listed once")]
    public void NothingFound()
    {
        Assert.Null(Find(_root));
        var searched = BuildCommand.RulebookSearchPaths(_root, _root);
        Assert.Equal(4, searched.Count);
        Assert.Equal(searched.Count, searched.Distinct().Count());
    }
}
