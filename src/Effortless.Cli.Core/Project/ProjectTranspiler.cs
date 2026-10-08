using Newtonsoft.Json;

namespace Effortless.Cli.Project;

public class ProjectTranspiler
{
    public ProjectTranspiler()
    {
        ProjectTranspilerId = Guid.NewGuid();
    }

    public ProjectTranspiler(
        string relativePath,
        string toolName,
        string toolDisplayName,
        string pinnedVersion = null)
        : this()
    {
        var localCommand = string.IsNullOrEmpty(toolName);
        Name = localCommand ? "LocalCommand" : (toolDisplayName ?? toolName);
        PinnedVersion = pinnedVersion;
        RelativePath = (relativePath ?? string.Empty).Replace("\\", "/");
        CommandLine = CaptureCommandLine(Environment.CommandLine);
    }

    public static string CaptureCommandLine(string sourceCommandLine)
    {
        var lowerCli = sourceCommandLine.ToLower().Replace("\\", "/");
        var commandLine = sourceCommandLine;
        if (lowerCli.Contains("/ssotme.exe"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/ssotme.exe") + "/ssotme.exe".Length);
        else if (lowerCli.Contains("/effortless.exe"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/effortless.exe") + "/effortless.exe".Length);
        else if (lowerCli.Contains("/aicapture.exe"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/aicapture.exe") + "/aicapture.exe".Length);
        else if (lowerCli.Contains("/aic.exe"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/aic.exe") + "/aic.exe".Length);
        else if (lowerCli.Contains("/effortless.cli.dll"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/effortless.cli.dll") + "/effortless.cli.dll".Length);
        // The published npm install runs the self-contained Effortless.Cli(.exe) binary.
        else if (lowerCli.Contains("/effortless.cli.exe"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/effortless.cli.exe") + "/effortless.cli.exe".Length);
        else if (lowerCli.Contains("/effortless.cli"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/effortless.cli") + "/effortless.cli".Length);
        else if (lowerCli.Contains("/ssotme.ost.cli.dll"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/ssotme.ost.cli.dll") + "/ssotme.ost.cli.dll".Length);
        else if (lowerCli.Contains("/aicapture.ost.cli.dll"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/aicapture.ost.cli.dll") + "/aicapture.ost.cli.dll".Length);
        else if (lowerCli.Contains("/ssotme"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/ssotme") + "/ssotme".Length);
        else if (lowerCli.Contains("/aicapture"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/aicapture") + "/aicapture".Length);
        else if (lowerCli.Contains("/effortless"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/effortless") + "/effortless".Length);
        else if (lowerCli.Contains("/aic"))
            commandLine = commandLine.Substring(lowerCli.IndexOf("/aic") + "/aic".Length);

        commandLine = commandLine.Trim(" '\"".ToCharArray());
        if (commandLine.StartsWith("/ssotme "))
            commandLine = commandLine.Substring("/ssotme ".Length);
        if (commandLine.StartsWith("/aic "))
            commandLine = commandLine.Substring("/aic ".Length);
        if (commandLine.StartsWith("/aicapture "))
            commandLine = commandLine.Substring("/aicapture ".Length);
        if (commandLine.StartsWith("/effortless "))
            commandLine = commandLine.Substring("/effortless ".Length);
        if (commandLine.StartsWith("install "))
            commandLine = commandLine.Substring("install ".Length);
        if (commandLine.StartsWith("-install "))
            commandLine = commandLine.Substring("-install ".Length);
        if (commandLine.StartsWith("ssotme "))
            commandLine = commandLine.Substring("ssotme ".Length);
        if (commandLine.StartsWith("aic "))
            commandLine = commandLine.Substring("aic ".Length);
        if (commandLine.StartsWith("aicapture "))
            commandLine = commandLine.Substring("aicapture ".Length);
        if (commandLine.StartsWith("effortless "))
            commandLine = commandLine.Substring("effortless ".Length);
        if (commandLine.StartsWith("effortless.exe "))
            commandLine = commandLine.Substring("effortless.exe ".Length);
        commandLine = commandLine.Trim(" '\"".ToCharArray());
        if (commandLine.StartsWith("install "))
            commandLine = commandLine.Substring("install ".Length);
        if (commandLine.StartsWith("-install "))
            commandLine = commandLine.Substring("-install ".Length);
        // -install says what to do with the command; it is never part of the step that builds.
        commandLine = string.Join(" ", commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Equals("-install", StringComparison.OrdinalIgnoreCase)));

        Console.WriteLine($"COMMAND LINE: {commandLine}");
        return commandLine;
    }

    [JsonIgnore]
    public Transpiler MatchedTranspiler { get; set; }

    public bool IsSSoTTranspiler { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "ProjectTranspilerId")]
    public Guid ProjectTranspilerId { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "SSoTmeProjectId")]
    public Guid SSoTmeProjectId { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "Name")]
    public string Name { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "RelativePath")]
    public string RelativePath { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "CommandLine")]
    public string CommandLine { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "CreatedOn")]
    public DateTime? CreatedOn { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "LastTranspilerRequestId")]
    public Guid LastTranspilerRequestId { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include, PropertyName = "IsDisabled")]
    public bool IsDisabled { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "SortOrder")]
    public int SortOrder { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "TranspilerGroup")]
    public string TranspilerGroup { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "PinnedVersion")]
    public string PinnedVersion { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "LastVersionUsed")]
    public string LastVersionUsed { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate, PropertyName = "LastUrl")]
    public string LastUrl { get; set; }

    public override string ToString()
    {
        var files = string.Format("{0}", RelativePath).PadRight(40);
        return string.Format("{0} :: {1}", files, CommandLine);
    }

    internal bool SyncCommandLineVersion()
    {
        if (string.IsNullOrEmpty(PinnedVersion) || string.IsNullOrEmpty(CommandLine))
            return false;

        var parts = CommandLine.Split(' ');
        var toolPart = parts[0];
        var segments = toolPart.Split('/');
        if (segments.Length < 2)
            return false;

        var lastSegment = segments[segments.Length - 1];
        if (!lastSegment.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            || lastSegment.Length < 2
            || !char.IsDigit(lastSegment[1]))
        {
            return false;
        }

        if (string.Equals(lastSegment, PinnedVersion, StringComparison.OrdinalIgnoreCase))
            return false;

        segments[segments.Length - 1] = PinnedVersion;
        parts[0] = string.Join("/", segments);
        CommandLine = string.Join(" ", parts);
        return true;
    }

    internal bool HasCommandLineVersion()
    {
        if (string.IsNullOrWhiteSpace(CommandLine))
        {
            return false;
        }

        var toolPart = CommandLine.Split(' ')[0];
        var slash = toolPart.LastIndexOf('/');
        if (slash < 0 || slash == toolPart.Length - 1)
        {
            return false;
        }

        var suffix = toolPart[(slash + 1)..];
        return suffix.Length > 1
            && suffix[0] is 'v' or 'V'
            && char.IsDigit(suffix[1]);
    }

    internal bool ClearCommandLineVersion()
    {
        if (!HasCommandLineVersion())
        {
            return false;
        }

        var parts = CommandLine.Split(' ');
        parts[0] = parts[0][..parts[0].LastIndexOf('/')];
        CommandLine = string.Join(" ", parts);
        return true;
    }

    public void Describe(EffortlessProject project)
    {
        Console.WriteLine("\n-----------------------------------");
        Console.WriteLine("---- {0}{1}", Name, IsDisabled ? "    **** DISABLED ****" : "");
        Console.WriteLine("---- .{0}/", $"{RelativePath}".Replace("\\", "/"));
        Console.WriteLine("-----------------------------------");
        Console.WriteLine("\nCommand Line:> effortless {0}\n", CommandLine);
    }

    public bool IsAtPath(string relativePath, bool exactMatch = false)
    {
        relativePath = relativePath.Replace("\\", "/").Trim('/').ToLower();
        var transpilerPath = (RelativePath ?? string.Empty)
            .Replace("\\", "/")
            .Trim('/')
            .ToLower();

        if (string.IsNullOrEmpty(relativePath))
        {
            if (exactMatch)
                return string.IsNullOrEmpty(transpilerPath);
            return true;
        }

        if (string.IsNullOrEmpty(transpilerPath))
            return false;

        if (exactMatch)
            return transpilerPath == relativePath;

        return transpilerPath.StartsWith(relativePath) || transpilerPath == relativePath;
    }

    public string GetProjectRelativePath(EffortlessProject project)
    {
        var fullPath = Path.Combine(project.RootPath, RelativePath.Trim("\\/".ToCharArray()));
        return fullPath.Substring(project.RootPath.Length).Replace("\\", "/");
    }
}
