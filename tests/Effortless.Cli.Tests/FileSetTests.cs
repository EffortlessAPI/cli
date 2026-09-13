using System.ComponentModel;
using System.Text;
using Effortless.Cli.FileSets;
using Effortless.Cli.Project;

namespace Effortless.Cli.Tests;

public sealed class FileSetTests
{
    [Fact(DisplayName = "unit-fileset-xml-roundtrip: FileSet XML round-trips every content kind")]
    public void FileSetXmlRoundTripsEveryContentKind()
    {
        var source = new FileSet
        {
            CreatedOn = new DateTime(2026, 8, 30, 12, 34, 56, DateTimeKind.Utc),
            FileSetFiles = new BindingList<FileSetFile>
            {
                new()
                {
                    RelativePath = "plain.txt",
                    OriginalRelativePath = "input/plain.txt",
                    FileContents = "plain",
                    AlwaysOverwrite = true,
                    OverwriteMode = "Always",
                    SkipClean = true,
                },
                new() { RelativePath = "legacy.txt", ZippedFileContents = GZip.Zip("legacy") },
                new() { RelativePath = "binary.bin", BinaryFileContents = [0, 1, 255] },
                new() { RelativePath = "text.txt", ZippedTextFileContents = GZip.Zip("text") },
                new() { RelativePath = "zipped.bin", ZippedBinaryFileContents = GZip.Zip(new byte[] { 255, 1, 0 }) },
                new() { RelativePath = "empty.txt" },
            },
        };

        var xml = FileSetXml.ToXml(source);
        var actual = FileSetXml.ToFileSet(xml);

        Assert.Contains("<FileSetFiles>", xml, StringComparison.Ordinal);
        Assert.Contains("<ZippedFileContents>", xml, StringComparison.Ordinal);
        Assert.Contains("<ZippedTextFileContents>", xml, StringComparison.Ordinal);
        Assert.Contains("<BinaryFileContents>", xml, StringComparison.Ordinal);
        Assert.Contains("<ZippedBinaryFileContents>", xml, StringComparison.Ordinal);
        Assert.Equal(source.FileSetId, actual.FileSetId);
        Assert.Equal(source.CreatedOn, actual.CreatedOn);
        Assert.Equal(source.FileSetFiles.Count, actual.FileSetFiles.Count);

        for (var index = 0; index < source.FileSetFiles.Count; index++)
        {
            var expected = source.FileSetFiles[index];
            var observed = actual.FileSetFiles[index];
            Assert.Equal(expected.RelativePath, observed.RelativePath);
            Assert.Equal(expected.OriginalRelativePath, observed.OriginalRelativePath);
            Assert.Equal(expected.FileContents, observed.FileContents);
            Assert.Equal(expected.ZippedFileContents, observed.ZippedFileContents);
            Assert.Equal(expected.BinaryFileContents, observed.BinaryFileContents);
            Assert.Equal(expected.ZippedTextFileContents, observed.ZippedTextFileContents);
            Assert.Equal(expected.ZippedBinaryFileContents, observed.ZippedBinaryFileContents);
            Assert.Equal(expected.AlwaysOverwrite, observed.AlwaysOverwrite);
            Assert.Equal(expected.OverwriteMode, observed.OverwriteMode);
            Assert.Equal(expected.SkipClean, observed.SkipClean);
        }
    }

    [Fact(DisplayName = "unit-gzip: text and bytes round-trip through gzip")]
    public void TextAndBytesRoundTripThroughGzip()
    {
        const string unicode = "Effortless café 🚀";

        Assert.Equal(unicode, unicode.Zip().UnzipToString());
        Assert.Equal(string.Empty, string.Empty.Zip().UnzipToString());
        Assert.Equal(
            new byte[] { 0, 1, 2, 127, 128, 255 },
            new byte[] { 0, 1, 2, 127, 128, 255 }.Zip().Unzip());
        Assert.Equal(string.Empty, ((byte[])null!).UnzipToString());
    }

    [Fact(DisplayName = "unit-is-binary: file classification preserves the legacy control-character heuristic")]
    public void FileClassificationPreservesLegacyControlCharacterHeuristic()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("text.txt"), "plain text");
        File.WriteAllText(
            directory.File("utf8-bom.txt"),
            "café",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllBytes(
            directory.File("image.png"),
            [0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x0E, 0x0A]);
        File.WriteAllBytes(directory.File("empty.bin"), []);

        Assert.False(new FileInfo(directory.File("text.txt")).IsBinaryFile());
        Assert.False(new FileInfo(directory.File("utf8-bom.txt")).IsBinaryFile());
        Assert.True(new FileInfo(directory.File("image.png")).IsBinaryFile());
        Assert.False(new FileInfo(directory.File("empty.bin")).IsBinaryFile());
    }

    [Fact(DisplayName = "unit-split-fileset-rules: FileSet writer honors overwrite and content rules")]
    public void FileSetWriterHonorsOverwriteAndContentRules()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("never.txt"), "local edits");
        File.WriteAllText(directory.File("always-mode.txt"), "old");

        var files = new FileSet
        {
            FileSetFiles = new BindingList<FileSetFile>
            {
                new() { RelativePath = "plain.txt", FileContents = "plain", AlwaysOverwrite = true },
                new() { RelativePath = "never.txt", FileContents = "server", OverwriteMode = "Never" },
                new() { RelativePath = "zipped.txt", ZippedTextFileContents = GZip.Zip("zipped"), AlwaysOverwrite = true },
                new() { RelativePath = "zipped.bin", ZippedBinaryFileContents = GZip.Zip(new byte[] { 0, 1, 255 }), AlwaysOverwrite = true },
                new() { RelativePath = "always-mode.txt", FileContents = "new", OverwriteMode = "Always" },
                new() { RelativePath = "binary.bin", BinaryFileContents = [255, 1, 0], AlwaysOverwrite = true },
            },
        };

        FileSetWriter.SplitFileSetXml(FileSetXml.ToXml(files), overwriteAll: false, directory.Path);

        Assert.Equal("plain", File.ReadAllText(directory.File("plain.txt")));
        Assert.Equal("local edits", File.ReadAllText(directory.File("never.txt")));
        Assert.Equal("zipped" + Environment.NewLine, File.ReadAllText(directory.File("zipped.txt")));
        Assert.Equal(new byte[] { 0, 1, 255 }, File.ReadAllBytes(directory.File("zipped.bin")));
        Assert.Equal("new", File.ReadAllText(directory.File("always-mode.txt")));
        Assert.Equal(new byte[] { 255, 1, 0 }, File.ReadAllBytes(directory.File("binary.bin")));

        var invalid = new FileSet
        {
            FileSetFiles = new BindingList<FileSetFile>
            {
                new() { RelativePath = "missing.txt" },
            },
        };
        var exception = Assert.Throws<Exception>(
            () => FileSetWriter.SplitFileSetXml(FileSetXml.ToXml(invalid), overwriteAll: false, directory.Path));
        Assert.Contains("without content nodes", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "unit-clean-fileset-rules: FileSet cleaner removes Always entries and stamped Never entries still unchanged")]
    public void FileSetCleanerRemovesAlwaysAndUnchangedStampedNeverEntries()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.File("delete"));
        Directory.CreateDirectory(directory.File("keep"));
        Directory.CreateDirectory(directory.File("skip"));
        File.WriteAllText(directory.File("delete/always.txt"), "generated");
        File.WriteAllText(directory.File("delete/untouched.txt"), "generated");
        File.WriteAllText(directory.File("keep/never.txt"), "edited");
        File.WriteAllText(directory.File("keep/unstamped.txt"), "generated");
        File.WriteAllText(directory.File("skip/skip.txt"), "generated");

        var ledger = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new() { RelativePath = "delete/always.txt", FileContents = "generated", AlwaysOverwrite = true },
                    new() { RelativePath = "delete/untouched.txt", FileContents = "generated", OverwriteMode = "Never" },
                    new() { RelativePath = "keep/never.txt", FileContents = "generated", OverwriteMode = "Never" },
                    new() { RelativePath = "keep/unstamped.txt", FileContents = "generated", OverwriteMode = "Never" },
                    new()
                    {
                        RelativePath = "skip/skip.txt",
                        FileContents = "generated",
                        AlwaysOverwrite = true,
                        SkipClean = true,
                    },
                },
            });

        // keep/unstamped.txt stays unstamped: the shape of a ledger written before the stamp.
        ledger = StampCleanIfUnchanged(ledger, "delete/untouched.txt", "keep/never.txt");

        var original = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory.Path;
            FileSetCleaner.CleanFileSet(ledger, debug: false, deleteEmptyDirs: true, deleteUnchangedNever: true);
        }
        finally
        {
            Environment.CurrentDirectory = original;
        }

        Assert.False(File.Exists(directory.File("delete/always.txt")));
        Assert.False(File.Exists(directory.File("delete/untouched.txt")));
        Assert.False(Directory.Exists(directory.File("delete")));
        Assert.Equal("edited", File.ReadAllText(directory.File("keep/never.txt")));
        Assert.Equal("generated", File.ReadAllText(directory.File("keep/unstamped.txt")));
        Assert.Equal("generated", File.ReadAllText(directory.File("skip/skip.txt")));
    }

    [Fact(DisplayName = "unit-clean-if-unchanged-stamp: Never outputs are stamped CleanIfUnchanged, a Never output at an input path gets SkipClean")]
    public void MarkCleanIfUnchangedStampsGeneratedNeverFilesAndProtectsInputs()
    {
        using var directory = new TestDirectory();
        var project = new EffortlessProject { RootPath = directory.Path };
        var extractToDir = Path.Combine(directory.Path, "effortless-rulebook");
        Directory.CreateDirectory(extractToDir);

        var inputXml = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new()
                    {
                        RelativePath = "effortless-rulebook.json",
                        OriginalRelativePath = "effortless-rulebook/effortless-rulebook.json",
                        ZippedFileContents = GZip.Zip("original"),
                    },
                },
            });
        var outputXml = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new() { RelativePath = "effortless-rulebook.json", FileContents = "upserted", OverwriteMode = "Always" },
                    new() { RelativePath = "scaffold.cs", FileContents = "partial", OverwriteMode = "Never" },
                    new() { RelativePath = "generated.cs", FileContents = "base", AlwaysOverwrite = true },
                    new() { RelativePath = "declared.txt", FileContents = "kept", OverwriteMode = "Never", SkipClean = true },
                },
            });

        var downgraded = ZfsLedger.DowngradeInPlaceUpserts(project, inputXml, outputXml, extractToDir, out _);
        var stamped = ZfsLedger.MarkCleanIfUnchanged(project, inputXml, downgraded, extractToDir);

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(stamped.Substring(stamped.IndexOf("<")));
        string Child(string path, string name) =>
            doc.SelectSingleNode($"//FileSetFile[RelativePath='{path}']/{name}")?.InnerText;

        Assert.Equal("true", Child("effortless-rulebook.json", "SkipClean"));
        Assert.Null(Child("effortless-rulebook.json", "CleanIfUnchanged"));
        Assert.Equal("true", Child("scaffold.cs", "CleanIfUnchanged"));
        Assert.Null(Child("generated.cs", "CleanIfUnchanged"));
        Assert.Null(Child("declared.txt", "CleanIfUnchanged"));

        // The unedited rulebook matches the ledger byte for byte and must still survive clean.
        File.WriteAllText(Path.Combine(extractToDir, "effortless-rulebook.json"), "upserted");
        File.WriteAllText(Path.Combine(extractToDir, "scaffold.cs"), "partial");
        var original = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = extractToDir;
            FileSetCleaner.CleanFileSet(stamped, debug: false, deleteEmptyDirs: false, deleteUnchangedNever: true);
        }
        finally
        {
            Environment.CurrentDirectory = original;
        }

        Assert.Equal("upserted", File.ReadAllText(Path.Combine(extractToDir, "effortless-rulebook.json")));
        Assert.False(File.Exists(Path.Combine(extractToDir, "scaffold.cs")));
    }

    private static string StampCleanIfUnchanged(string fileSetXml, params string[] relativePaths)
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(fileSetXml.Substring(fileSetXml.IndexOf("<")));
        foreach (var path in relativePaths)
        {
            var entry = doc.SelectSingleNode($"//FileSetFile[RelativePath='{path}']");
            var stamp = doc.CreateElement("CleanIfUnchanged");
            stamp.InnerText = "true";
            entry.AppendChild(stamp);
        }

        return doc.OuterXml;
    }

    [Fact(DisplayName = "unit-self-source-overwrite-guard: blocks an Always overwrite when the input file changed on disk mid-run")]
    public void ValidateSelfSourceOverwritesBlocksStaleAlwaysOverwrite()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("input.txt"), "original");

        var project = new EffortlessProject { RootPath = directory.Path };
        var inputXml = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new()
                    {
                        RelativePath = "input.txt",
                        OriginalRelativePath = "input.txt",
                        ZippedFileContents = GZip.Zip("original"),
                    },
                },
            });
        var outputXml = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new() { RelativePath = "input.txt", FileContents = "transformed", OverwriteMode = "Always" },
                },
            });

        ZfsLedger.ValidateSelfSourceOverwrites(project, inputXml, outputXml, directory.Path);

        File.WriteAllText(directory.File("input.txt"), "concurrently edited");

        var exception = Assert.Throws<Exception>(
            () => ZfsLedger.ValidateSelfSourceOverwrites(project, inputXml, outputXml, directory.Path));
        Assert.Contains("changed on disk", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "unit-self-source-overwrite-guard: ignores Never mode and paths unrelated to the input")]
    public void ValidateSelfSourceOverwritesIgnoresNonAlwaysAndUnrelatedPaths()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("input.txt"), "original");

        var project = new EffortlessProject { RootPath = directory.Path };
        var inputXml = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new()
                    {
                        RelativePath = "input.txt",
                        OriginalRelativePath = "input.txt",
                        ZippedFileContents = GZip.Zip("original"),
                    },
                },
            });

        File.WriteAllText(directory.File("input.txt"), "concurrently edited");

        var neverModeOutputXml = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new() { RelativePath = "input.txt", FileContents = "transformed", OverwriteMode = "Never" },
                },
            });
        var unrelatedOutputXml = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new() { RelativePath = "output.txt", FileContents = "generated", OverwriteMode = "Always" },
                },
            });

        ZfsLedger.ValidateSelfSourceOverwrites(project, inputXml, neverModeOutputXml, directory.Path);
        ZfsLedger.ValidateSelfSourceOverwrites(project, inputXml, unrelatedOutputXml, directory.Path);
    }

    [Fact(DisplayName = "unit-in-place-upsert-downgrade: an Always output at the input's own path becomes Never so it is never ledgered as generated")]
    public void DowngradeInPlaceUpsertsRewritesAlwaysToNeverAtTheInputPath()
    {
        using var directory = new TestDirectory();
        var project = new EffortlessProject { RootPath = directory.Path };
        var extractToDir = Path.Combine(directory.Path, "effortless-rulebook");
        Directory.CreateDirectory(extractToDir);

        // The tool was handed effortless-rulebook/effortless-rulebook.json and returned
        // the same file, at the same place, declaring Always: the in-place upsert shape.
        var inputXml = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new()
                    {
                        RelativePath = "effortless-rulebook.json",
                        OriginalRelativePath = "effortless-rulebook/effortless-rulebook.json",
                        ZippedFileContents = GZip.Zip("original"),
                    },
                },
            });
        var outputXml = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new()
                    {
                        RelativePath = "effortless-rulebook.json",
                        FileContents = "upserted",
                        OverwriteMode = "Always",
                    },
                },
            });

        var downgraded = ZfsLedger.DowngradeInPlaceUpserts(
            project,
            inputXml,
            outputXml,
            extractToDir,
            out var upsertPaths);

        var expectedPath = new FileInfo(
            Path.Combine(extractToDir, "effortless-rulebook.json")).FullName;
        Assert.Contains(expectedPath, upsertPaths);

        var rewritten = FileSetXml.ToFileSet(downgraded).FileSetFiles.Single();
        Assert.Equal("Never", rewritten.OverwriteMode);
        Assert.False(rewritten.AlwaysOverwrite);

        // The whole point of the downgrade: the clean pass must now refuse to delete the
        // user's hand-edited file, which is what deleted the rulebook before this rule.
        File.WriteAllText(Path.Combine(extractToDir, "effortless-rulebook.json"), "hand edited");
        var original = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = extractToDir;
            FileSetCleaner.CleanFileSet(downgraded, debug: false, deleteEmptyDirs: false);
        }
        finally
        {
            Environment.CurrentDirectory = original;
        }

        Assert.Equal(
            "hand edited",
            File.ReadAllText(Path.Combine(extractToDir, "effortless-rulebook.json")));
    }

    [Fact(DisplayName = "unit-in-place-upsert-downgrade: a normal generated output keeps Always and is left fully alone")]
    public void DowngradeInPlaceUpsertsLeavesOrdinaryGeneratedOutputsUntouched()
    {
        using var directory = new TestDirectory();
        var project = new EffortlessProject { RootPath = directory.Path };

        // rulebook-to-rulespeak's shape: reads the rulebook, writes different files.
        var inputXml = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new()
                    {
                        RelativePath = "effortless-rulebook.json",
                        OriginalRelativePath = "effortless-rulebook/effortless-rulebook.json",
                        ZippedFileContents = GZip.Zip("original"),
                    },
                },
            });
        var outputXml = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new() { RelativePath = "rulespeak.md", FileContents = "docs", AlwaysOverwrite = true },
                },
            });

        var result = ZfsLedger.DowngradeInPlaceUpserts(
            project,
            inputXml,
            outputXml,
            Path.Combine(directory.Path, "rulespeak"),
            out var upsertPaths);

        Assert.Empty(upsertPaths);
        Assert.Same(outputXml, result);
        Assert.True(FileSetXml.ToFileSet(result).FileSetFiles.Single().AlwaysOverwrite);
    }

    [Fact(DisplayName = "unit-in-place-upsert-write: a downgraded entry is still written over the existing file")]
    public void ForcedPathsAreWrittenEvenThoughTheEntrySaysNever()
    {
        using var directory = new TestDirectory();
        var target = directory.File("effortless-rulebook.json");
        File.WriteAllText(target, "hand edited");

        // After the downgrade the entry says Never, which would normally skip an existing
        // file; the upsert still has to land, so the resolved path is forced.
        var xml = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new()
                    {
                        RelativePath = "effortless-rulebook.json",
                        FileContents = "upserted",
                        OverwriteMode = "Never",
                    },
                },
            });

        var forced = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            new FileInfo(target).FullName,
        };

        xml.SplitFileSetXml(false, directory.Path, forced);
        Assert.Equal("upserted", File.ReadAllText(target));

        // Without the force set the Never entry must still be respected.
        File.WriteAllText(target, "hand edited again");
        xml.SplitFileSetXml(false, directory.Path, null);
        Assert.Equal("hand edited again", File.ReadAllText(target));
    }

    [Fact(DisplayName = "unit-in-place-upsert-ledger-prune: a ledger poisoned before this rule drops the entry and self-heals")]
    public void PruneLedgerEntriesRemovesPreviouslyPoisonedRows()
    {
        using var directory = new TestDirectory();

        // What an older build wrote: the user's own rulebook recorded as a generated
        // Always file, alongside a genuinely generated one that must survive the prune.
        var poisoned = FileSetXml.ToXml(
            new FileSet
            {
                FileSetFiles = new BindingList<FileSetFile>
                {
                    new()
                    {
                        RelativePath = "effortless-rulebook.json",
                        FileContents = "generated",
                        AlwaysOverwrite = true,
                    },
                    new() { RelativePath = "rulespeak.md", FileContents = "docs", AlwaysOverwrite = true },
                },
            });

        var prune = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            new FileInfo(directory.File("effortless-rulebook.json")).FullName,
        };

        var pruned = ZfsLedger.PruneLedgerEntries(poisoned, prune, directory.Path);
        var remaining = FileSetXml.ToFileSet(pruned).FileSetFiles;

        Assert.Equal("rulespeak.md", Assert.Single(remaining).RelativePath);
    }
}
