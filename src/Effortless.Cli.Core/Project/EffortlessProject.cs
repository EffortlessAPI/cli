using System.ComponentModel;
using Effortless.Cli;
using Effortless.Cli.Text;
using Newtonsoft.Json;

namespace Effortless.Cli.Project;

public class EffortlessProject
{
    private string _currentPath;

    public EffortlessProject()
    {
        HiddenPaths = new List<string>();
        ExpandedPaths = new List<string>();
        SSoTmeProjectId = Guid.NewGuid().ToString();
        ProjectSettings = new BindingList<ProjectSetting>();
        ProjectTranspilers = new BindingList<ProjectTranspiler>();
    }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "SSoTmeProjectId")]
    public string SSoTmeProjectId { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "Name")]
    public string Name { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "Description")]
    public string Description { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "CreatedOn")]
    public DateTime? CreatedOn { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "RootPath")]
    public string RootPath { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "ProjectSettings")]
    public BindingList<ProjectSetting> ProjectSettings { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "ProjectTranspilers")]
    public BindingList<ProjectTranspiler> ProjectTranspilers { get; set; }

    /// <summary>
    /// Set when ProjectLocator renames a legacy ssotme.json to effortless.json.
    /// The first catalog check then disables steps whose tools no longer exist
    /// and clears this flag.
    /// </summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore, PropertyName = "MigratedFromSsotme")]
    public bool MigratedFromSsotme { get; set; }

    public List<string> HiddenPaths { get; set; }

    public List<string> ExpandedPaths { get; set; }

    public bool ShowHidden { get; set; }

    public bool ShowAllFiles { get; set; }

    public string CurrentPath
    {
        get => _currentPath;
        set
        {
            if (!string.Equals(_currentPath, value, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(_currentPath) || !string.IsNullOrEmpty(value))
                {
                    _currentPath = value;
                }
            }
        }
    }

    public List<string> SSoTmeProjectFiles { get; private set; }

    public void Save()
    {
        ProjectFileStore.Save(this);
    }

    internal void Save(DirectoryInfo rootDirectory)
    {
        ProjectFileStore.Save(this, rootDirectory);
    }

    public string GenerateNewProject()
    {
        if (string.IsNullOrEmpty(SSoTmeProjectId) || SSoTmeProjectId == Guid.Empty.ToString())
        {
            SSoTmeProjectId = Guid.NewGuid().ToString();
        }

        return SSoTmeProjectId;
    }

    public void RemoveUUIds()
    {
        (ProjectSettings ?? Enumerable.Empty<ProjectSetting>()).ToList().ForEach(setting =>
        {
            setting.ProjectSettingId = Guid.Empty;
        });

        (ProjectTranspilers ?? Enumerable.Empty<ProjectTranspiler>()).ToList().ForEach(transpiler =>
        {
            if (transpiler != null)
            {
                transpiler.ProjectTranspilerId = Guid.Empty;
            }
        });
    }

    internal DirectoryInfo GetZFSDI(string relativePath)
    {
        var ledgerDirectory = GetEffortlessDI();
        var zfsDirectory = new DirectoryInfo(
            Path.Combine(
                ledgerDirectory.FullName,
                relativePath.Trim("\\/".ToCharArray())));
        if (!zfsDirectory.Exists)
        {
            zfsDirectory.Create();
        }

        return zfsDirectory;
    }

    public const string LedgerDirectoryName = ".effortless";
    public const string LegacyLedgerDirectoryName = ".ssotme";

    /// <summary>
    /// The per-project build-state directory <c>&lt;root&gt;/.effortless</c>
    /// (zfs ledgers, temp filesets). A legacy <c>.ssotme</c> directory is
    /// renamed in place the first time it is seen (D28, option A).
    /// </summary>
    public DirectoryInfo GetEffortlessDI()
    {
        MigrateLegacyLedgerDirectory(RootPath);
        var ledgerDirectory = new DirectoryInfo(Path.Combine(RootPath, LedgerDirectoryName));
        if (!ledgerDirectory.Exists)
        {
            ledgerDirectory.Create();
            ledgerDirectory.Attributes =
                FileAttributes.Directory | FileAttributes.Hidden;
        }

        return ledgerDirectory;
    }

    /// <summary>
    /// Renames <c>&lt;root&gt;/.ssotme</c> to <c>&lt;root&gt;/.effortless</c> when only the
    /// legacy directory exists. It is gitignored build state, so a rename is safe.
    /// </summary>
    public static void MigrateLegacyLedgerDirectory(string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath))
        {
            return;
        }

        var target = new DirectoryInfo(Path.Combine(rootPath, LedgerDirectoryName));
        var legacy = new DirectoryInfo(Path.Combine(rootPath, LegacyLedgerDirectoryName));
        if (target.Exists || !legacy.Exists)
        {
            return;
        }

        legacy.MoveTo(target.FullName);
    }

    public void AddSetting(string setting)
    {
        var partsOfSetting = setting.SafeToString()
            .Split("=".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);

        var settingName = partsOfSetting.FirstOrDefault();
        var settingValue = string.Join(string.Empty, partsOfSetting.Skip(1));

        if (string.IsNullOrEmpty(settingName))
        {
            throw new Exception("Settings must be in the format of 'name=value'");
        }

        if (ProjectSettings == null)
        {
            ProjectSettings = new BindingList<ProjectSetting>();
        }

        var currentSettings = ProjectSettings.Where(
            projectSetting => string.Equals(
                projectSetting.Name,
                settingName,
                StringComparison.OrdinalIgnoreCase));

        var addSetting = true;
        if (currentSettings.Count() == 1 && currentSettings.First().Value == settingValue)
        {
            addSetting = false;
        }

        if (addSetting)
        {
            currentSettings.ToList().ForEach(
                settingToRemove => ProjectSettings.Remove(settingToRemove));

            ProjectSettings.Add(
                new ProjectSetting
                {
                    Name = settingName,
                    Value = settingValue,
                });
            Console.WriteLine(
                "Added Setting: {0}: '{1}'",
                settingName,
                settingValue);
        }

        if (string.Equals(
                settingName,
                "project-name",
                StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(settingValue) && settingValue != Name)
            {
                Name = settingValue;
            }
        }
    }

    public void RemoveSetting(string setting)
    {
        var partsOfSetting = setting.SafeToString()
            .Split("=".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);

        var settingName = partsOfSetting.FirstOrDefault();
        var settingValue = string.Join(string.Empty, partsOfSetting.Skip(1));

        if (string.IsNullOrEmpty(settingName))
        {
            throw new Exception("Setting name not provided - unable to remove.");
        }

        var matchingSetting = ProjectSettings.FirstOrDefault(
            projectSetting => projectSetting.Name.Equals(
                settingName,
                StringComparison.OrdinalIgnoreCase));
        if (ReferenceEquals(matchingSetting, null))
        {
            throw new Exception(string.Format(
                "Can't find matching setting: {0}",
                settingName));
        }

        ProjectSettings.Remove(matchingSetting);
        Console.WriteLine(
            "Successfully Removed Setting: {0}: '{1}'",
            matchingSetting.Name,
            matchingSetting.Value);
    }

    internal void ListSettings()
    {
        Console.WriteLine("\nSETTINGS: ");

        if (ProjectSettings.Any())
        {
            foreach (var projectSetting in ProjectSettings)
            {
                Console.WriteLine(
                    "    - {0} = {1}",
                    projectSetting.Name,
                    projectSetting.Value);
            }
        }
        else
        {
            Console.WriteLine("NO settings added to the project yet.");
        }
    }

    public string GetName()
    {
        if (string.IsNullOrEmpty(Name))
        {
            return Path.GetFileName(RootPath);
        }

        return Name;
    }

    public void Describe(string relativePath = "", bool exactMatch = false)
    {
        Console.WriteLine("\n==========================================");
        Console.WriteLine("======  {0}", Name);
        Console.WriteLine("======    {0}", RootPath);
        Console.WriteLine("==========================================");
        Console.WriteLine(string.Empty);

        ListSettings();
        Console.WriteLine(string.Empty);
        Console.WriteLine("\nTRANSPILERS: ");

        var matchingProjectTranspilers = ProjectTranspilers.ToList();
        if (!string.IsNullOrEmpty(relativePath))
        {
            relativePath = GetProjectRelativePath(relativePath);
            matchingProjectTranspilers = matchingProjectTranspilers
                .Where(projectTranspiler =>
                    projectTranspiler.IsAtPath(relativePath, exactMatch))
                .ToList();
        }

        foreach (var projectTranspiler in matchingProjectTranspilers)
        {
            projectTranspiler.Describe(this);
        }
    }

    public void Install(
        TranspilePayload result,
        string transpilerGroup,
        string pinnedVersion = null)
    {
        string currentDirectory;
        try
        {
            currentDirectory = Environment.CurrentDirectory;
        }
        catch (Exception)
        {
            currentDirectory = RootPath;
        }

        var relativePath = GetProjectRelativePath(currentDirectory);
        var localCommand = ReferenceEquals(result, null);
        var projectTranspiler = new ProjectTranspiler(
            relativePath,
            localCommand ? null : result.Transpiler.Name,
            localCommand ? null : result.Transpiler.DisplayName,
            pinnedVersion)
        {
            MatchedTranspiler = localCommand ? null : result.Transpiler,
            TranspilerGroup = transpilerGroup,
        };

        IntegrateNewTranspiler(projectTranspiler);
        Save();
    }

    internal void Update(ProjectTranspiler projectTranspiler, TranspilePayload result)
    {
        projectTranspiler.MatchedTranspiler =
            ReferenceEquals(result, null) ? null : result.Transpiler;
        IntegrateExistingTranspiler(projectTranspiler);
        Save();
    }

    internal void UpdateRuntimeOnly(
        ProjectTranspiler projectTranspiler,
        TranspilePayload result)
    {
        projectTranspiler.MatchedTranspiler =
            ReferenceEquals(result, null) ? null : result.Transpiler;
    }

    public void Uninstall(string transpilerName, string transpilerGroup = null)
    {
        string currentDirectory;
        try
        {
            currentDirectory = Environment.CurrentDirectory;
        }
        catch (Exception)
        {
            currentDirectory = RootPath;
        }

        var relativePath = GetProjectRelativePath(currentDirectory);
        var transpilersToRemove =
            FindMatchingTranspilers(transpilerName, relativePath, transpilerGroup);

        if (transpilersToRemove.Count > 1)
        {
            CliLog.LogLine(
                $"Warning: '{transpilerName}' matched multiple tools. Provide a fully-qualified name to target a specific tool.",
                ConsoleColor.Yellow);
            foreach (var transpiler in transpilersToRemove)
            {
                CliLog.LogLine(
                    $"  - {transpiler.CommandLine}",
                    ConsoleColor.Yellow);
            }
        }
        else if (transpilersToRemove.Count == 1)
        {
            var transpiler = transpilersToRemove[0];
            var zfsDirectory = GetZFSDI(transpiler.RelativePath);
            // R12: a local tool's ledger is keyed local-<name>.
            var zfsName = LocalTools.LocalToolCatalog.Discover(RootPath)
                    .Match(GetToolName(transpiler.CommandLine))?.LedgerKey
                ?? NameHelpers.LowerHyphenName(transpiler.Name);
            var zfsFile = new FileInfo(
                Path.Combine(zfsDirectory.FullName, zfsName + ".zfs"));

            if (zfsFile.Exists)
            {
                Console.Write(
                    "Would you like to delete all files generated by this tool? (y/n): ");
                var response = Console.ReadLine()?.Trim().ToLower();
                if (response == "y" || response == "yes")
                {
                    var originalDirectory = Environment.CurrentDirectory;
                    try
                    {
                        var toolDirectory = new DirectoryInfo(
                            Path.Combine(
                                RootPath,
                                transpiler.RelativePath.Trim("\\/".ToCharArray())));
                        if (toolDirectory.Exists)
                        {
                            Environment.CurrentDirectory = toolDirectory.FullName;
                        }

                        CleanRunner.CleanTranspiler(
                            transpiler,
                            this,
                            preserveZfs: false);
                        CliLog.LogLine(
                            "Cleaned generated files.",
                            ConsoleColor.Green);
                    }
                    finally
                    {
                        Environment.CurrentDirectory = originalDirectory;
                    }
                }
            }

            ProjectTranspilers.Remove(transpiler);
            CliLog.LogTranspiler(
                "Uninstalled Transpiler",
                ConsoleColor.Red,
                transpiler.CommandLine,
                transpiler.RelativePath,
                transpiler.TranspilerGroup);
            Save();
        }
        else
        {
            CliLog.LogLine(
                $"No tools matching tool name '{transpilerName}' in path '{relativePath}'",
                ConsoleColor.Yellow);
        }
    }

    public string GetProjectRelativePath(DirectoryInfo directory)
    {
        if (ReferenceEquals(directory, null))
        {
            return "/";
        }

        return GetProjectRelativePath(directory.FullName);
    }

    public string GetProjectRelativePath(string fullPath)
    {
        var relativePathDirectory = new DirectoryInfo(fullPath);
        var rootPathDirectory = new DirectoryInfo(RootPath);
        var relativePath = relativePathDirectory.FullName.Substring(
            rootPathDirectory.FullName.Length);
        return relativePath.Replace("\\", "/");
    }

    private void IntegrateExistingTranspiler(ProjectTranspiler projectTranspiler)
    {
        IntegrateTranspiler(projectTranspiler, false);
    }

    private void IntegrateNewTranspiler(
        ProjectTranspiler projectTranspiler)
    {
        IntegrateTranspiler(projectTranspiler, true);
    }

    internal void IntegrateTranspiler(
        ProjectTranspiler projectTranspiler,
        bool addIfMissing)
    {
        var toolName = GetToolName(projectTranspiler.CommandLine);
        var matches = FindMatchingTranspilers(
            toolName,
            projectTranspiler.RelativePath,
            projectTranspiler.TranspilerGroup);

        if (matches.Count > 1)
        {
            CliLog.LogLine(
                $"Warning: '{toolName}' matched multiple installed tools. Provide a fully-qualified name to target a specific tool.",
                ConsoleColor.Yellow);
            foreach (var transpiler in matches)
            {
                CliLog.LogLine(
                    $"  - {transpiler.CommandLine}",
                    ConsoleColor.Yellow);
            }

            return;
        }

        var matchingTranspiler = matches.FirstOrDefault();
        var firstIndex = -1;
        if (!ReferenceEquals(matchingTranspiler, null))
        {
            CliLog.LogTranspiler(
                "Replaced Transpiler",
                ConsoleColor.Yellow,
                projectTranspiler.CommandLine,
                projectTranspiler.RelativePath,
                projectTranspiler.TranspilerGroup);
            firstIndex = ProjectTranspilers.IndexOf(matchingTranspiler);
            ProjectTranspilers.Remove(matchingTranspiler);
        }
        else
        {
            CliLog.LogTranspiler(
                "Installed Transpiler",
                ConsoleColor.Green,
                projectTranspiler.CommandLine,
                projectTranspiler.RelativePath,
                projectTranspiler.TranspilerGroup);
        }

        firstIndex = Math.Min(firstIndex, ProjectTranspilers.Count);
        if (firstIndex >= 0)
        {
            ProjectTranspilers.Insert(firstIndex, projectTranspiler);
        }
        else if (addIfMissing)
        {
            ProjectTranspilers.Add(projectTranspiler);
        }

        projectTranspiler.Name =
            projectTranspiler.MatchedTranspiler?.DisplayName
            ?? projectTranspiler.MatchedTranspiler?.Name
            ?? "noTranspilerFound";
    }

    public static string GetToolName(string commandLine)
    {
        return commandLine?.Split(' ').FirstOrDefault() ?? "";
    }

    public static bool ToolNameMatches(
        string installedToolName,
        string providedName)
    {
        if (string.Equals(
                installedToolName,
                providedName,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (providedName.EndsWith(
                "/" + installedToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (installedToolName.EndsWith(
                "/" + providedName,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool VersionedToolNameMatches(
        string commandTool,
        string toolName)
    {
        if (string.Equals(
                commandTool,
                toolName,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var commandShort = commandTool.Contains('/')
            ? commandTool.Substring(commandTool.LastIndexOf('/') + 1)
            : commandTool;
        var toolShort = toolName.Contains('/')
            ? toolName.Substring(toolName.LastIndexOf('/') + 1)
            : toolName;
        if (string.Equals(
                commandShort,
                toolShort,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (commandTool.Contains('/'))
        {
            var commandWithoutVersion = commandTool.Substring(
                0,
                commandTool.LastIndexOf('/'));
            var commandToolName = commandWithoutVersion.Contains('/')
                ? commandWithoutVersion.Substring(
                    commandWithoutVersion.LastIndexOf('/') + 1)
                : commandWithoutVersion;
            if (string.Equals(
                    commandToolName,
                    toolShort,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    commandWithoutVersion,
                    toolName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal List<ProjectTranspiler> FindMatchingTranspilers(
        string toolName,
        string relativePath,
        string transpilerGroup)
    {
        return ProjectTranspilers
            .Where(projectTranspiler =>
                ToolNameMatches(
                    GetToolName(projectTranspiler.CommandLine),
                    toolName)
                && string.Equals(
                    projectTranspiler.RelativePath,
                    relativePath,
                    StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(transpilerGroup)
                    || string.Equals(
                        projectTranspiler.TranspilerGroup,
                        transpilerGroup,
                        StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
