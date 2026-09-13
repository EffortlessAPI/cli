using System.Net;
using System.Xml;
using Effortless.Cli.Text;

namespace Effortless.Cli.FileSets;

public static class FileSetCleaner
{
    public static void CleanZippedFileSet(
        this byte[] zippedFileSet,
        bool debug = false,
        bool deleteEmptyDirs = true,
        bool deleteUnchangedNever = false)
    {
        if (debug)
        {
            System.Console.WriteLine($"DEBUG: Unzipping {zippedFileSet.Length} bytes to string");
        }

        var fileSetXml = zippedFileSet.UnzipToString();
        if (debug)
        {
            System.Console.WriteLine(
                $"DEBUG: Unzipped XML length: {fileSetXml.Length} chars, calling CleanFileSet()");
        }

        fileSetXml.CleanFileSet(debug, deleteEmptyDirs, deleteUnchangedNever);
        if (debug)
        {
            System.Console.WriteLine("DEBUG: CleanFileSet() completed");
        }
    }

    public static void CleanFileSet(
        this string fileSetXml,
        bool debug,
        bool deleteEmptyDirs = true,
        bool deleteUnchangedNever = false)
    {
        if (debug)
        {
            System.Console.WriteLine(
                $"DEBUG: CleanFileSet called with XML (empty={string.IsNullOrEmpty(fileSetXml)})");
        }

        if (!string.IsNullOrEmpty(fileSetXml) && fileSetXml.Contains("<"))
        {
            if (debug)
            {
                System.Console.WriteLine("DEBUG: XML contains '<', preprocessing...");
            }

            fileSetXml = fileSetXml.Substring(fileSetXml.IndexOf("<"));
            fileSetXml = fileSetXml.Replace("FileContents><?xml ", "FileContents>&lt;?xml");
            XmlDocument doc = new XmlDocument();
            try
            {
                if (debug)
                {
                    System.Console.WriteLine("DEBUG: Loading XML into XmlDocument...");
                }

                doc.LoadXml(fileSetXml);
                if (debug)
                {
                    System.Console.WriteLine(
                        $"DEBUG: XML loaded successfully, root element: {doc.DocumentElement?.Name}");
                }
            }
            catch (Exception ex)
            {
                if (debug)
                {
                    System.Console.WriteLine($"DEBUG: XML parsing failed: {ex.Message}");
                    System.Console.WriteLine(
                        $"DEBUG: XML content preview: {(fileSetXml.Length > 200 ? fileSetXml.Substring(0, 200) + "..." : fileSetXml)}");
                }

                System.Console.WriteLine($"Warning: XML parsing failed: {ex.Message}");
                return;
            }

            var fileSetNodes = doc.SelectNodes("//FileSetFile");
            if (debug)
            {
                System.Console.WriteLine(
                    $"DEBUG: Found {fileSetNodes?.Count ?? 0} FileSetFile nodes to process");
            }

            foreach (XmlElement fileSetFileElem in fileSetNodes)
            {
                XmlNode relPathElem = fileSetFileElem.SelectSingleNode("RelativePath");
                if (!ReferenceEquals(relPathElem, null))
                {
                    if (debug)
                    {
                        System.Console.WriteLine($"DEBUG: Processing file: {relPathElem.InnerText}");
                    }

                    CleanFileByRelativeName(fileSetFileElem, relPathElem, debug, deleteUnchangedNever);
                }
                else if (debug)
                {
                    System.Console.WriteLine("DEBUG: FileSetFile node missing RelativePath");
                }
            }

            if (deleteEmptyDirs)
            {
                if (debug)
                {
                    System.Console.WriteLine("DEBUG: Calling CleanEmptyFolders()");
                }

                CleanEmptyFolders();
            }
        }
        else if (debug)
        {
            System.Console.WriteLine("DEBUG: XML is empty or doesn't contain '<' - skipping cleanup");
        }
    }

    private static void CleanEmptyFolders()
    {
        new DirectoryInfo(".").CleanEmptyFolders();
    }

    private static void CleanEmptyFolders(this DirectoryInfo di)
    {
        if (di.Exists)
        {
            foreach (DirectoryInfo diChildDir in di.GetDirectories())
            {
                CleanEmptyFolders(diChildDir);
            }

            if (!di.GetDirectories().Any() && !di.GetFiles().Any())
            {
                try
                {
                    di.Delete();
                }
                catch (IOException)
                {
                    // Ignore: new content may have appeared before the empty directory was removed.
                }
            }
        }
    }

    private static void CleanFileByRelativeName(
        XmlElement fileSetFileElem,
        XmlNode relPathElem,
        bool debug,
        bool deleteUnchangedNever)
    {
        if (debug)
        {
            System.Console.WriteLine(
                $"DEBUG: CleanFileByRelativeName called for: {relPathElem.InnerText}");
        }

        var skipElement = fileSetFileElem.SelectSingleNode(".//SkipClean");
        if (!ReferenceEquals(skipElement, null) &&
            string.Equals(skipElement.InnerText, "true", StringComparison.OrdinalIgnoreCase))
        {
            if (debug)
            {
                System.Console.WriteLine(
                    $"DEBUG: Skipping cleanup for {relPathElem.InnerText} (SkipClean=true)");
            }

            return;
        }

        var fullFileName = GetFullFileName(relPathElem.InnerText, new DirectoryInfo("."));
        if (debug)
        {
            System.Console.WriteLine($"DEBUG: Full file path resolved to: {fullFileName}");
        }

        FileInfo fiToClean = new FileInfo(fullFileName);
        if (debug)
        {
            System.Console.WriteLine($"DEBUG: File exists: {fiToClean.Exists}");
        }

        if (fiToClean.Exists)
        {
            bool neverOverwrite = true;

            XmlNode aoNode = fileSetFileElem.SelectSingleNode("AlwaysOverwrite");
            if (!ReferenceEquals(aoNode, null))
            {
                if (aoNode.InnerText == "true")
                {
                    neverOverwrite = false;
                }
            }

            XmlNode omNode = fileSetFileElem.SelectSingleNode("OverwriteMode");
            if (!ReferenceEquals(omNode, null))
            {
                if (string.Equals(omNode.InnerText, "Always", StringComparison.OrdinalIgnoreCase))
                {
                    neverOverwrite = false;
                }
                else if (string.Equals(omNode.InnerText, "Never", StringComparison.OrdinalIgnoreCase))
                {
                    neverOverwrite = true;
                }
            }

            XmlNode fileContentsNode = fileSetFileElem.SelectSingleNode("FileContents");
            XmlNode zippedFileContents = fileSetFileElem.SelectSingleNode("ZippedTextFileContents");
            if (ReferenceEquals(zippedFileContents, null))
            {
                zippedFileContents = fileSetFileElem.SelectSingleNode("ZippedFileContents");
            }

            XmlNode binaryFileContentsNode = fileSetFileElem.SelectSingleNode("BinaryFileContents");
            byte[] data = Array.Empty<byte>();
            bool binaryEquals = false;
            if (!ReferenceEquals(binaryFileContentsNode, null))
            {
                data = Convert.FromBase64String(binaryFileContentsNode.InnerText);
                byte[] binaryFileContents = File.ReadAllBytes(fiToClean.FullName);
                binaryEquals = data.SequenceEqual(binaryFileContents);
            }

            string value = string.Empty;
            if (!ReferenceEquals(fileContentsNode, null))
            {
                // Decode exactly as FileSetWriter does, or an untouched file never compares equal.
                value = WebUtility.HtmlDecode(fileContentsNode.InnerXml).UnwrapCDATA();
            }
            else if (!ReferenceEquals(zippedFileContents, null))
            {
                var unzippedContent = Convert.FromBase64String(
                        zippedFileContents.InnerXml)
                    .UnzipToString();
                value = unzippedContent + Environment.NewLine;
            }

            if (debug)
            {
                System.Console.WriteLine(
                    $"DEBUG: neverOverwrite={neverOverwrite}, hasFileContents={!ReferenceEquals(fileContentsNode, null)}, hasZippedContents={!ReferenceEquals(zippedFileContents, null)}, hasBinaryContents={!ReferenceEquals(binaryFileContentsNode, null)}");
            }

            bool contentMatches = false;
            bool hasAnyContent = !ReferenceEquals(fileContentsNode, null) ||
                                 !ReferenceEquals(zippedFileContents, null) ||
                                 !ReferenceEquals(binaryFileContentsNode, null);

            if (!hasAnyContent && debug)
            {
                System.Console.WriteLine(
                    $"WARNING: Cannot clean {fiToClean.FullName} - ZFS entry has no content nodes (FileContents, ZippedTextFileContents, ZippedFileContents, or BinaryFileContents)");
            }
            else if (!string.IsNullOrEmpty(value))
            {
                var fileContent = File.ReadAllText(fiToClean.FullName);
                contentMatches = fileContent == value;
                if (debug)
                {
                    System.Console.WriteLine($"DEBUG: Text content matches: {contentMatches}");
                }
            }
            else if (binaryEquals)
            {
                contentMatches = true;
                if (debug)
                {
                    System.Console.WriteLine($"DEBUG: Binary content matches: {contentMatches}");
                }
            }

            // A Never file is the user's to edit, so it survives clean once edited. While it
            // still matches what the tool generated it is ours, and an explicit clean removes
            // it (the pre-write pass of a build does not: Never is written once, never replaced).
            // Only entries the CLI stamped CleanIfUnchanged qualify (see ZfsLedger.MarkCleanIfUnchanged):
            // a ledger written before the stamp existed may hold the user's own rulebook as Never.
            XmlNode cleanIfUnchangedNode = fileSetFileElem.SelectSingleNode("CleanIfUnchanged");
            bool cleanIfUnchanged = deleteUnchangedNever &&
                                    !ReferenceEquals(cleanIfUnchangedNode, null) &&
                                    string.Equals(cleanIfUnchangedNode.InnerText, "true", StringComparison.OrdinalIgnoreCase);

            if (!neverOverwrite || (cleanIfUnchanged && contentMatches))
            {
                CliLog.Cleaning(fiToClean.FullName);
                if (debug)
                {
                    System.Console.WriteLine(
                        !neverOverwrite
                            ? "DEBUG: File deleted - Reason: AlwaysOverwrite=true"
                            : "DEBUG: File deleted - Reason: OverwriteMode=Never but unchanged since generated");
                }

                fiToClean.Delete();
                if (debug)
                {
                    System.Console.WriteLine("DEBUG: File deletion completed successfully");
                }
            }
            else if (debug)
            {
                System.Console.WriteLine(
                    cleanIfUnchanged
                        ? "DEBUG: File NOT deleted - Reason: OverwriteMode=Never and hand-edited"
                        : "DEBUG: File NOT deleted - Reason: OverwriteMode=Never (preserving hand-edits)");
            }
        }
    }

    private static string GetFullFileName(string relativeFileName, DirectoryInfo rootDI)
    {
        if (rootDI == null)
        {
            rootDI = new DirectoryInfo(".");
        }

        relativeFileName = relativeFileName.SafeToString()
            .Trim("\r\n\t \\/".ToCharArray());
        FileInfo fi = new FileInfo(Path.Combine(rootDI.FullName, relativeFileName));
        return fi.FullName;
    }
}
