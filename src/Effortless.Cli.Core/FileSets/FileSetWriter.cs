using System.Net;
using System.Text;
using System.Xml;
using Effortless.Cli.Text;

namespace Effortless.Cli.FileSets;

public static class FileSetWriter
{
    private const int ErrorSharingViolationHResult = -2147024864;

    public static void WriteTo(this FileSet fileSet, DirectoryInfo rootDirInfo)
    {
        fileSet.ToXml().SplitFileSetXml(false, rootDirInfo.FullName);
    }

    public static void SplitFileSetXml(this string fileSetXml, string basePath)
    {
        fileSetXml.SplitFileSetXml(false, basePath);
    }

    public static void SplitFileSetXml(this string fileSetXml, bool overwriteAll, string basePath)
    {
        fileSetXml.SplitFileSetXml(overwriteAll, basePath, null);
    }

    /// <summary>
    /// <paramref name="forceOverwritePaths"/> is the set of resolved full paths that must
    /// be written even though their FileSet entry says OverwriteMode=Never. It carries the
    /// in-place-upsert case (see <c>ZfsLedger.DowngradeInPlaceUpserts</c>): those entries
    /// were deliberately downgraded to Never so that nothing downstream ever treats the
    /// user's own file as generated, but the upsert the tool just computed still has to
    /// land on disk. This is a set of paths supplied by the caller for this one call, not
    /// a rule about any tool, shape, or path baked into the writer.
    /// </summary>
    public static void SplitFileSetXml(
        this string fileSetXml,
        bool overwriteAll,
        string basePath,
        HashSet<string> forceOverwritePaths)
    {
        if (string.IsNullOrEmpty(fileSetXml) || !fileSetXml.Contains("<"))
        {
            return;
        }

        fileSetXml = fileSetXml.Substring(fileSetXml.IndexOf("<"));
        fileSetXml = fileSetXml.Replace("FileContents><?xml ", "FileContents>&lt;?xml");
        XmlDocument doc = new XmlDocument();
        try
        {
            int len = fileSetXml.Length;
            fileSetXml = fileSetXml.Trim((char)65279);
            int len2 = fileSetXml.Length;
            doc.LoadXml(fileSetXml);
            if (doc.DocumentElement.Name == "FileSet")
            {
                foreach (XmlElement elem in doc.DocumentElement.SelectNodes("//FileSetFile"))
                {
                    bool neverOverwrite = true;

                    XmlNode aoNode = elem.SelectSingleNode("AlwaysOverwrite");
                    if (!ReferenceEquals(aoNode, null))
                    {
                        if (aoNode.InnerText == "true")
                        {
                            neverOverwrite = false;
                        }
                    }

                    XmlNode omNode = elem.SelectSingleNode("OverwriteMode");
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

                    XmlNode contentsNode = elem.SelectSingleNode("FileContents");
                    XmlNode binaryContentsNode = elem.SelectSingleNode("BinaryFileContents");
                    XmlNode zippedBinaryContentsNode = elem.SelectSingleNode("ZippedBinaryFileContents");
                    XmlNode zippedTextContentsNode = elem.SelectSingleNode("ZippedTextFileContents");
                    if (ReferenceEquals(zippedTextContentsNode, null))
                    {
                        zippedTextContentsNode = elem.SelectSingleNode("ZippedFileContents");
                    }

                    if (!ReferenceEquals(contentsNode, null))
                    {
                        string contents = contentsNode.InnerXml;
                        foreach (XmlElement fileName in elem.SelectNodes("RelativePath"))
                        {
                            string xmlFilePath = Path.Combine(basePath, "test.xml");
                            ProcessFileSetFile(
                                xmlFilePath,
                                overwriteAll,
                                elem,
                                contents,
                                fileName,
                                basePath,
                                forceOverwritePaths);
                        }
                    }
                    else if (!ReferenceEquals(binaryContentsNode, null) ||
                             !ReferenceEquals(zippedBinaryContentsNode, null) ||
                             !ReferenceEquals(zippedTextContentsNode, null))
                    {
                        string contents = string.Empty;
                        if (!ReferenceEquals(binaryContentsNode, null))
                        {
                            contents = binaryContentsNode.InnerXml;
                        }
                        else if (!ReferenceEquals(zippedBinaryContentsNode, null))
                        {
                            contents = zippedBinaryContentsNode.InnerXml;
                        }
                        else
                        {
                            contents = zippedTextContentsNode.InnerXml;
                        }

                        foreach (XmlElement fileName in elem.SelectNodes("RelativePath"))
                        {
                            byte[] data = Convert.FromBase64String(contents);

                            var finalName = Path.Combine(
                                basePath,
                                fileName.InnerText.SafeToString().Trim("/".ToCharArray()));
                            var fileInfo = new FileInfo(finalName);

                            if (!fileInfo.Directory.Exists)
                            {
                                fileInfo.Directory.Create();
                            }

                            if (!fileInfo.Exists ||
                                !neverOverwrite ||
                                IsForced(forceOverwritePaths, fileInfo.FullName))
                            {
                                if (!ReferenceEquals(zippedBinaryContentsNode, null))
                                {
                                    WriteAllBytes(fileInfo.FullName, data.Unzip());
                                }
                                else if (!ReferenceEquals(zippedTextContentsNode, null))
                                {
                                    using (StreamWriter sw = new StreamWriter(
                                               File.Open(fileInfo.FullName, FileMode.Create),
                                               new UTF8Encoding(false)))
                                    {
                                        sw.WriteLine(data.UnzipToString());
                                    }

                                    OnFileWritten(fileInfo.FullName);
                                }
                                else
                                {
                                    if (!fileInfo.Directory.Exists)
                                    {
                                        fileInfo.Directory.Create();
                                    }

                                    var di = new DirectoryInfo(fileInfo.FullName);
                                    if (di.Exists)
                                    {
                                        throw new Exception(
                                            string.Format(
                                                "Invalid filename for result file - {0} is a directory",
                                                fileInfo.FullName));
                                    }

                                    WriteAllBytes(fileInfo.FullName, data);
                                }
                            }
                        }
                    }
                    else
                    {
                        var fileNames = elem.SelectNodes("RelativePath")
                            .Cast<XmlElement>()
                            .Select(fn => fn.InnerText)
                            .ToArray();
                        if (fileNames.Any())
                        {
                            throw new Exception(
                                $"Transpiler error: FileSet contains file entries without content nodes. Files: {string.Join(", ", fileNames)}. Expected at least one of: FileContents, ZippedTextFileContents, ZippedFileContents, or BinaryFileContents.");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw ex;
        }
    }

    private static bool IsForced(HashSet<string> forceOverwritePaths, string fullPath)
    {
        return forceOverwritePaths is not null &&
               forceOverwritePaths.Contains(fullPath);
    }

    private static void ProcessFileSetFile(
        string relativePathOfXml,
        bool overwriteAll,
        XmlElement elem,
        string contents,
        XmlElement fileName,
        string basePath,
        HashSet<string> forceOverwritePaths = null)
    {
        string relativeFileName = fileName.InnerText;
        relativeFileName = FullFromRelative(
            relativePathOfXml,
            relativeFileName.Trim("\\/ \r\n".ToCharArray()));
        string dir = Path.GetDirectoryName(relativeFileName);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        bool fileExists = File.Exists(relativeFileName);
        bool writeFile = true;
        bool neverOverwrite = true;

        XmlNode aoNode = elem.SelectSingleNode("AlwaysOverwrite");
        if (!ReferenceEquals(aoNode, null))
        {
            if (aoNode.InnerText == "true")
            {
                neverOverwrite = false;
            }
        }

        XmlNode omNode = elem.SelectSingleNode("OverwriteMode");
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

        if (fileExists && neverOverwrite)
        {
            writeFile = false;
        }

        if (IsForced(forceOverwritePaths, relativeFileName))
        {
            writeFile = true;
        }

        while (contents.Contains("[$$NEWUUID$$]"))
        {
            contents = string.Format(
                "{0}{1}{2}",
                contents.Substring(0, contents.IndexOf("[$$NEWUUID$$]")),
                Guid.NewGuid(),
                contents.Substring(contents.IndexOf("[$$NEWUUID$$]") + "[$$NEWUUID$$]".Length));
        }

        string decodedContent = WebUtility.HtmlDecode(contents);
        decodedContent = decodedContent.UnwrapCDATA();

        if (overwriteAll || writeFile)
        {
            if (overwriteAll ||
                !File.Exists(relativeFileName) ||
                File.ReadAllText(relativeFileName) != decodedContent)
            {
                WriteAllText(relativeFileName, decodedContent);
            }
        }
    }

    public static void SplitFileSetFile(this string fileSetFileName, string basePath)
    {
        fileSetFileName.SplitFileSetFile(basePath, null);
    }

    public static void SplitFileSetFile(
        this string fileSetFileName,
        string basePath,
        HashSet<string> forceOverwritePaths)
    {
        const int maxRetries = 5;
        const int retryDelayMs = 100;
        int retryCount = 0;
        IOException lastException = null;

        while (retryCount < maxRetries)
        {
            try
            {
                using (var fileStream = new FileStream(
                           fileSetFileName,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.ReadWrite))
                using (var streamReader = new StreamReader(fileStream, Encoding.UTF8))
                {
                    string fileContents = streamReader.ReadToEnd();
                    SplitFileSetXml(fileContents, false, basePath, forceOverwritePaths);
                    return;
                }
            }
            catch (IOException ex)
            {
                lastException = ex;
                if (IsFileLockException(ex))
                {
                    retryCount++;
                    if (retryCount < maxRetries)
                    {
                        Thread.Sleep(retryDelayMs * retryCount);
                    }
                }
                else
                {
                    throw;
                }
            }
        }

        if (lastException != null)
        {
            throw new IOException(
                $"Failed to read from file '{fileSetFileName}' after {maxRetries} attempts: {lastException.Message}",
                lastException);
        }
    }

    private static void OnFileWritten(string fileName)
    {
        CliLog.Writing(fileName);
    }

    private static void WriteAllBytes(string fileName, byte[] fileBytes)
    {
        File.WriteAllBytes(fileName, fileBytes);
        OnFileWritten(fileName);
    }

    private static void WriteAllText(string fileName, string fileContents)
    {
        var fi = new FileInfo(fileName);
        if (!fi.Directory.Exists)
        {
            fi.Directory.Create();
        }

        const int maxRetries = 5;
        const int retryDelayMs = 100;
        int retryCount = 0;
        bool success = false;

        IOException lastException = null;
        while (!success && retryCount < maxRetries)
        {
            try
            {
                using (StreamWriter sw = new StreamWriter(
                           File.Open(
                               fileName,
                               FileMode.Create,
                               FileAccess.Write,
                               FileShare.ReadWrite),
                           new UTF8Encoding(false)))
                {
                    sw.Write(fileContents.UnwrapCDATA());
                    sw.Flush();
                }

                success = true;
            }
            catch (IOException ex)
            {
                lastException = ex;

                if (IsFileLockException(ex) && retryCount < maxRetries - 1)
                {
                    retryCount++;
                    Thread.Sleep(retryDelayMs * retryCount);
                }
                else if (!IsFileLockException(ex))
                {
                    throw;
                }
                else
                {
                    retryCount++;
                }
            }
        }

        if (success)
        {
            OnFileWritten(fileName);
        }
        else if (lastException != null)
        {
            throw new IOException(
                $"Failed to write to file '{fileName}' after {maxRetries} attempts: {lastException.Message}",
                lastException);
        }
    }

    private static bool IsFileLockException(IOException ex)
    {
        return ex.Message.Contains("being used by another process") ||
               ex.Message.Contains("access is denied") ||
               ex.HResult == ErrorSharingViolationHResult;
    }

    public static string FullFromRelative(this string rootFullPath, string relativeFileName)
    {
        relativeFileName = relativeFileName.SafeToString().Trim("\r\n\t \\/".ToCharArray());
        FileInfo fi = new FileInfo(Path.Combine(Path.GetDirectoryName(rootFullPath), relativeFileName));
        return fi.FullName;
    }

    public static string RelativeFromFull(this string rootFullPath, string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return fullPath;
        }

        string dir = Path.GetDirectoryName(rootFullPath).Replace("/", "\\");
        string[] dirParts = dir.Split("\\".ToCharArray());
        string[] fullPathParts = fullPath.Replace("/", "\\").Split("\\".ToCharArray());
        int index = 0;
        while (index < fullPathParts.Length && index < dirParts.Length)
        {
            if (string.Equals(fullPathParts[index], dirParts[index], StringComparison.OrdinalIgnoreCase))
            {
                index++;
            }
            else
            {
                break;
            }
        }

        string left = string.Join("\\", dirParts.Take(index));
        string[] parts = left.Split("\\/".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        string right = string.Join("\\", fullPathParts.Skip(index));
        int count = dirParts.Length - index;
        for (int i = 0; i < count; i++)
        {
            right = "../" + right;
        }

        return right.Replace("/", "\\");
    }

    public static string FindClosest(this string fullFileName, string otherFile)
    {
        string dir = Path.GetDirectoryName(fullFileName);
        string combinedPath = Path.Combine(dir, otherFile);
        Uri absoluteUri = new Uri(string.Format("file:///{0}", combinedPath));
        string fileName = absoluteUri.LocalPath;
        string relativeFileName = fullFileName.RelativeFromFull(fileName);
        bool lookDown = true;
        int count = 0;
        while (!File.Exists(fileName) && count < 10)
        {
            if (relativeFileName.StartsWith("../") && lookDown)
            {
                fileName = fullFileName.FullFromRelative(relativeFileName.Substring(3));
            }
            else
            {
                lookDown = false;
                fileName = fullFileName.FullFromRelative("../" + relativeFileName);
            }

            relativeFileName = fullFileName.RelativeFromFull(fileName);
            count++;
        }

        if (!File.Exists(fileName))
        {
            fileName = absoluteUri.LocalPath;
        }

        return fileName;
    }

    public static bool IsBinaryFile(this FileInfo fi)
    {
        long length = fi.Length;
        if (length == 0)
        {
            return false;
        }

        using (StreamReader stream = new StreamReader(fi.FullName))
        {
            int ch;
            while ((ch = stream.Read()) != -1)
            {
                if (isControlChar(ch))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool isControlChar(int ch)
    {
        return ch > Chars.NUL && ch < Chars.BS ||
               ch > Chars.CR && ch < Chars.SUB;
    }

    public static class Chars
    {
        public static char NUL = (char)0;
        public static char BS = (char)8;
        public static char CR = (char)13;
        public static char SUB = (char)26;
    }
}
