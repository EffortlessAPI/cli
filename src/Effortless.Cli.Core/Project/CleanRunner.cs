using Effortless.Cli.FileSets;
using Effortless.Cli.Text;

namespace Effortless.Cli.Project;

public class CleanRunner
{
    private readonly EffortlessProject _project;
    private LocalTools.LocalToolCatalog _localTools;
    private List<string> _projectFiles = new List<string>();

    public CleanRunner(EffortlessProject project)
    {
        _project = project;
    }

    public void CleanAll(
        bool preserveZFS,
        bool purge,
        bool debugOption,
        bool withSubprojects = false)
    {
        var rootPathDirectory =
            new DirectoryInfo(_project.RootPath);
        Clean(
            rootPathDirectory.FullName,
            preserveZFS,
            purge,
            debugOption,
            true,
            withSubprojects: withSubprojects);
        new EmptyFolderPruner(_project)
            .RemoveEmptyFolders(rootPathDirectory.FullName);
    }

    public static void CleanTranspiler(
        ProjectTranspiler projectTranspiler,
        EffortlessProject project,
        bool preserveZfs,
        bool debug = false)
    {
        new CleanRunner(project).Clean(
            projectTranspiler,
            preserveZfs,
            debug);
    }

    public void Clean(
        string pathFullName,
        bool preserveZFS,
        bool purge,
        bool debugOption,
        bool cleanAll = false,
        bool cleanLocal = false,
        bool withSubprojects = false)
    {
        string currentDirectory;
        try
        {
            currentDirectory = Environment.CurrentDirectory;
        }
        catch (Exception)
        {
            currentDirectory = pathFullName;
        }

        // D6/D12: cleanAll stops at nested project boundaries; only
        // cleanWithSubprojects walks into them.
        if (withSubprojects)
        {
            _projectFiles = NestedProjectFinder.Find(_project.RootPath)
                .Select(directory => directory.FullName)
                .ToList();
        }

        try
        {
            var relativePath =
                _project.GetProjectRelativePath(pathFullName);
            var matchingProjectTranspilers =
                _project.ProjectTranspilers.Where(
                    projectTranspiler =>
                        projectTranspiler.IsAtPath(
                            relativePath,
                            exactMatch: cleanLocal));

            foreach (var projectTranspiler in
                     matchingProjectTranspilers)
            {
                Clean(
                    projectTranspiler,
                    preserveZFS,
                    debugOption);

                try
                {
                    var testDirectory =
                        Environment.CurrentDirectory;
                }
                catch (Exception)
                {
                    if (Directory.Exists(_project.RootPath))
                    {
                        Environment.CurrentDirectory =
                            _project.RootPath;
                    }
                }
            }

            if (withSubprojects)
            {
                CleanSubProjects();
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(currentDirectory))
                {
                    Environment.CurrentDirectory =
                        currentDirectory;
                }
                else if (Directory.Exists(_project.RootPath))
                {
                    Environment.CurrentDirectory =
                        _project.RootPath;
                }
            }
            catch (Exception)
            {
                if (Directory.Exists(_project.RootPath))
                {
                    Environment.CurrentDirectory =
                        _project.RootPath;
                }
            }
        }

        if (!preserveZFS && purge)
        {
            try
            {
                RemoveUnusedZFSFiles(debugOption);
            }
            catch (Exception ex)
            {
                if (debugOption)
                {
                    Console.WriteLine(
                        $"DEBUG: RemoveUnusedZFSFiles exception: {ex.Message}");
                }
            }
        }

        if (Directory.Exists(currentDirectory))
        {
            new EmptyFolderPruner(_project)
                .RemoveEmptyFolders(currentDirectory);
        }
        else if (Directory.Exists(pathFullName))
        {
            new EmptyFolderPruner(_project)
                .RemoveEmptyFolders(pathFullName);
        }
    }

    private void Clean(
        ProjectTranspiler projectTranspiler,
        bool preserveZFS,
        bool debug = false)
    {
        if (debug)
        {
            Console.WriteLine(
                "CLEANING: " +
                projectTranspiler.RelativePath +
                ": " +
                projectTranspiler.Name);
        }

        var directory = new DirectoryInfo(
            Path.Combine(
                _project.RootPath,
                projectTranspiler.RelativePath.Trim(
                    "\\/".ToCharArray())));
        if (!directory.Exists)
        {
            directory.Create();
        }

        Environment.CurrentDirectory = directory.FullName;
        var zfsDirectory =
            _project.GetZFSDI(projectTranspiler.RelativePath);

        string transpilerName = LocalLedgerKey(projectTranspiler);

        if (String.IsNullOrEmpty(transpilerName)
            && !String.IsNullOrEmpty(projectTranspiler.LastUrl))
        {
            transpilerName =
                projectTranspiler.LastUrl
                    .SanitizeUrlForFilename();
            if (debug)
            {
                Console.WriteLine(
                    $"DEBUG: Using LastUrl for ZFS lookup: {projectTranspiler.LastUrl} -> {transpilerName}");
            }
        }

        if (String.IsNullOrEmpty(transpilerName) &&
            IsRemoteUrlCommandLine(
                projectTranspiler.CommandLine))
        {
            var trimmedCommandLine =
                projectTranspiler.CommandLine.Trim();
            string targetUrl = null;

            if (trimmedCommandLine.StartsWith(
                    "http://",
                    StringComparison.OrdinalIgnoreCase) ||
                trimmedCommandLine.StartsWith(
                    "https://",
                    StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmedCommandLine.Split(' ');
                targetUrl = parts[0];
            }
            else if (trimmedCommandLine.Contains("-g "))
            {
                var parts =
                    projectTranspiler.CommandLine.Split(' ');
                var gIndex = Array.IndexOf(parts, "-g");
                if (gIndex >= 0 &&
                    gIndex + 1 < parts.Length)
                {
                    targetUrl = parts[gIndex + 1];
                }
            }

            if (!string.IsNullOrEmpty(targetUrl))
            {
                transpilerName =
                    targetUrl.SanitizeUrlForFilename();
            }
        }

        if (String.IsNullOrEmpty(transpilerName))
        {
            transpilerName =
                NameHelpers.LowerHyphenName(
                    projectTranspiler.Name);
        }

        string zfsFileName = String.Format(
            "{0}/{1}.zfs",
            zfsDirectory.FullName,
            transpilerName);
        var zfsFile = new FileInfo(zfsFileName);

        if (!zfsFile.Exists && zfsDirectory.Exists)
        {
            var zfsFiles =
                zfsDirectory.GetFiles("*.zfs");
            if (zfsFiles.Length == 1)
            {
                zfsFile = zfsFiles[0];
                if (debug)
                {
                    Console.WriteLine(
                        $"DEBUG: Expected ZFS '{zfsFileName}' not found, using '{zfsFile.FullName}'");
                }
            }
            else if (zfsFiles.Length > 1 && debug)
            {
                Console.WriteLine(
                    $"DEBUG: Expected ZFS '{zfsFileName}' not found, and {zfsFiles.Length} .zfs files exist — cannot determine which to clean");
            }
        }

        if (zfsFile.Exists)
        {
            var zippedFileSet =
                File.ReadAllBytes(zfsFile.FullName);
            if (debug)
            {
                Console.WriteLine(
                    $"DEBUG: Read {zippedFileSet.Length} bytes from {zfsFile.FullName}, calling CleanZippedFileSet()");
            }

            zippedFileSet.CleanZippedFileSet(debug, deleteUnchangedNever: true);
            if (debug)
            {
                Console.WriteLine(
                    "DEBUG: CleanZippedFileSet() completed");
            }

            if (!preserveZFS)
            {
                File.Delete(zfsFile.FullName);
            }
        }

        string xmlFileName = String.Format(
            "{0}/{1}.xml",
            zfsDirectory.FullName,
            transpilerName);
        var xmlFile = new FileInfo(xmlFileName);
        if (xmlFile.Exists)
        {
            if (debug)
            {
                Console.WriteLine(
                    $"DEBUG: Removing debug XML file: {xmlFile.FullName}");
            }

            xmlFile.Delete();
        }
    }

    private void RemoveUnusedZFSFiles(bool debug)
    {
        try
        {
            var ledgerDirectory =
                _project.GetEffortlessDI();
            if (!ledgerDirectory.Exists)
            {
                return;
            }

            var allZfsFiles = ledgerDirectory.GetFiles(
                "*.zfs",
                SearchOption.AllDirectories);

            var expectedZfsFiles = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var projectTranspiler in
                     _project.ProjectTranspilers)
            {
                string transpilerName =
                    LocalLedgerKey(projectTranspiler)
                    ?? NameHelpers.LowerHyphenName(
                        projectTranspiler.Name);
                if (transpilerName.StartsWith(
                        LocalTools.LocalToolCatalog.LedgerKeyPrefix,
                        StringComparison.Ordinal))
                {
                    // R12: local tools are keyed local-<name>, never by URL.
                }
                else if (IsRemoteUrlCommandLine(
                        projectTranspiler.CommandLine))
                {
                    var trimmedCommandLine =
                        projectTranspiler.CommandLine.Trim();
                    string targetUrl = null;

                    if (trimmedCommandLine.StartsWith(
                            "http://",
                            StringComparison.OrdinalIgnoreCase) ||
                        trimmedCommandLine.StartsWith(
                            "https://",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var parts =
                            trimmedCommandLine.Split(' ');
                        targetUrl = parts[0];
                    }
                    else if (trimmedCommandLine.Contains("-g "))
                    {
                        var parts =
                            projectTranspiler.CommandLine
                                .Split(' ');
                        var gIndex =
                            Array.IndexOf(parts, "-g");
                        if (gIndex >= 0 &&
                            gIndex + 1 < parts.Length)
                        {
                            targetUrl = parts[gIndex + 1];
                        }
                    }

                    if (!string.IsNullOrEmpty(targetUrl))
                    {
                        transpilerName =
                            targetUrl
                                .SanitizeUrlForFilename();
                    }
                }

                var zfsDirectory =
                    _project.GetZFSDI(
                        projectTranspiler.RelativePath);
                var expectedZfsPath = Path.Combine(
                    zfsDirectory.FullName,
                    $"{transpilerName}.zfs");
                expectedZfsFiles.Add(expectedZfsPath);
            }

            foreach (var zfsFile in allZfsFiles)
            {
                if (expectedZfsFiles.Contains(
                        zfsFile.FullName))
                {
                    continue;
                }

                if (debug)
                {
                    Console.WriteLine(
                        $"Processing orphaned ZFS file: {zfsFile.FullName}");
                }

                var ledgerDirectoryPath =
                    ledgerDirectory.FullName;
                var zfsFileDirectory =
                    zfsFile.DirectoryName;
                var relativePath = zfsFileDirectory
                    .Substring(ledgerDirectoryPath.Length)
                    .Trim(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                var transpilerWorkingDirectory =
                    Path.Combine(
                        _project.RootPath,
                        relativePath);

                string savedCurrentDirectory;
                try
                {
                    savedCurrentDirectory =
                        Environment.CurrentDirectory;
                }
                catch (Exception)
                {
                    savedCurrentDirectory =
                        _project.RootPath;
                }

                try
                {
                    if (Directory.Exists(
                            transpilerWorkingDirectory))
                    {
                        Environment.CurrentDirectory =
                            transpilerWorkingDirectory;
                        if (debug)
                        {
                            Console.WriteLine(
                                $"DEBUG: Changed to transpiler directory: {transpilerWorkingDirectory}");
                        }
                    }
                    else if (debug)
                    {
                        Console.WriteLine(
                            $"DEBUG: Transpiler directory doesn't exist: {transpilerWorkingDirectory}, using current directory");
                    }

                    var zippedFileSet =
                        File.ReadAllBytes(zfsFile.FullName);
                    if (debug)
                    {
                        Console.WriteLine(
                            $"DEBUG: Read {zippedFileSet.Length} bytes from orphaned ZFS, calling CleanZippedFileSet()");
                    }

                    zippedFileSet.CleanZippedFileSet(debug, deleteUnchangedNever: true);
                    if (debug)
                    {
                        Console.WriteLine(
                            "DEBUG: CleanZippedFileSet() completed for orphaned ZFS");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Warning: Error cleaning orphaned ZFS file {zfsFile.FullName}: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(
                                savedCurrentDirectory))
                        {
                            Environment.CurrentDirectory =
                                savedCurrentDirectory;
                        }
                        else if (Directory.Exists(
                                     _project.RootPath))
                        {
                            Environment.CurrentDirectory =
                                _project.RootPath;
                        }
                    }
                    catch (Exception)
                    {
                        if (Directory.Exists(
                                _project.RootPath))
                        {
                            try
                            {
                                Environment.CurrentDirectory =
                                    _project.RootPath;
                            }
                            catch
                            {
                            }
                        }
                    }

                    try
                    {
                        if (debug)
                        {
                            Console.WriteLine(
                                $"Removing orphaned ZFS file: {zfsFile.FullName}");
                        }

                        zfsFile.Delete();

                        var xmlFile = new FileInfo(
                            Path.ChangeExtension(
                                zfsFile.FullName,
                                ".xml"));
                        if (xmlFile.Exists)
                        {
                            if (debug)
                            {
                                Console.WriteLine(
                                    $"Removing orphaned debug XML file: {xmlFile.FullName}");
                            }

                            xmlFile.Delete();
                        }
                    }
                    catch (Exception ex)
                    {
                        if (debug)
                        {
                            Console.WriteLine(
                                $"Warning: Could not delete orphaned ZFS file: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Warning: Error cleaning unused ZFS files: {ex.Message}");
        }
    }

    private void CleanSubProjects()
    {
        foreach (var projectDirectory in _projectFiles)
        {
            new DirectoryInfo(projectDirectory).InvokeEffortlessClean();
        }
    }

    /// <summary>
    /// R12: a step whose tool is a project-local tool keeps its ledger under
    /// local-&lt;name&gt; regardless of the host port it ran against.
    /// </summary>
    private string LocalLedgerKey(ProjectTranspiler projectTranspiler)
    {
        var toolName = EffortlessProject.GetToolName(
            projectTranspiler.CommandLine);
        if (string.IsNullOrWhiteSpace(toolName)
            || string.IsNullOrWhiteSpace(_project.RootPath))
        {
            return null;
        }

        _localTools ??= LocalTools.LocalToolCatalog.Discover(
            _project.RootPath);
        return _localTools.Match(toolName)?.LedgerKey;
    }

    private static bool IsRemoteUrlCommandLine(
        string commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return false;
        }

        var trimmedCommandLine = commandLine.Trim();

        if (trimmedCommandLine.StartsWith(
                "http://",
                StringComparison.OrdinalIgnoreCase) ||
            trimmedCommandLine.StartsWith(
                "https://",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmedCommandLine.Contains("-g "))
        {
            var parts = trimmedCommandLine.Split(' ');
            var gIndex = Array.IndexOf(parts, "-g");
            if (gIndex >= 0 &&
                gIndex + 1 < parts.Length)
            {
                var nextPart = parts[gIndex + 1];
                return nextPart.StartsWith(
                           "http://",
                           StringComparison.OrdinalIgnoreCase) ||
                       nextPart.StartsWith(
                           "https://",
                           StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }
}
