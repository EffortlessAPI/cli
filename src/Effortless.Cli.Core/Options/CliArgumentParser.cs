using System.Reflection;
using Plossum.CommandLine;

namespace Effortless.Cli.Options;

public sealed class CliArgumentParser
{
    private static readonly IReadOnlySet<string> KnownOptionNames =
        GetKnownOptionNames();

    private static readonly IReadOnlyDictionary<string, string> NoDashCommandForms =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["listSettings"] = "listSettings",
            ["ls"] = "listSettings",
            ["addSetting"] = "addSetting",
            ["as"] = "addSetting",
            ["removeSetting"] = "removeSetting",
            ["rs"] = "removeSetting",
            ["execute"] = "execute",
            ["exec"] = "execute",
            ["setAccountAPIKey"] = "setAccountAPIKey",
            ["api"] = "setAccountAPIKey",
            ["listVersions"] = "listVersions",
            ["lv"] = "listVersions",
            ["l"] = "listVersions",
            ["checkVersion"] = "checkVersion",
            ["cv"] = "checkVersion",
            ["uc"] = "checkVersion",
            ["update"] = "checkVersion",
            ["pin"] = "pin",
            ["compileOnSave"] = "compileOnSave",
            ["cos"] = "compileOnSave",
            ["buildOnSave"] = "buildOnSave",
            ["bos"] = "buildOnSave",
            ["rebuildAllOnSave"] = "rebuildAllOnSave",
        };

    /// <summary>
    /// What -compileOnSave / -buildOnSave / -rebuildAllOnSave hold when the
    /// user names no file: search for the rulebook instead (see
    /// BuildCommand.FindRulebookToWatch). Not a path; never shown to the user.
    /// </summary>
    public const string FindRulebookToWatch = "<find-rulebook>";

    private static readonly IReadOnlySet<string> SaveWatchFlags =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-compileOnSave",
            "-cos",
            "-buildOnSave",
            "-bos",
            "-rebuildAllOnSave",
        };

    private static readonly IReadOnlySet<string> InputFlags =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-input",
            "-i",
        };

    public CliInvocation Parse(string[] argv)
    {
        var normalizedArguments = NormalizeArguments(argv ?? Array.Empty<string>());
        return ParseCore(normalizedArguments);
    }

    public CliInvocation Parse(string commandLine)
    {
        var options = new CliOptions();
        var parser = new CommandLineParser(options);
        parser.Parse(
            NormalizeLeadingNoDashCommand(commandLine ?? string.Empty),
            false);
        return CreateInvocation(options, parser);
    }

    private static CliInvocation ParseCore(string[] arguments)
    {
        var options = new CliOptions();
        var parser = new CommandLineParser(options);
        parser.Parse(ToPlossumCommandLine(arguments), false);
        return CreateInvocation(options, parser);
    }

    private static CliInvocation CreateInvocation(
        CliOptions options,
        CommandLineParser parser)
    {
        var remainingArguments = parser.RemainingArguments.ToList();
        var invocation = new CliInvocation
        {
            Options = options,
            RemainingArguments = remainingArguments,
            HasErrors = parser.HasErrors,
            ErrorText = parser.UsageInfo.GetErrorsAsString(78),
            UsageHeader = parser.UsageInfo.GetHeaderAsString(GetSafeHelpWidth()),
            UsageOptions = parser.UsageInfo.GetOptionsAsString(GetSafeHelpWidth()),
            ParseResult = parser.HasErrors ? -1 : 0,
            SuppressTranspile = parser.HasErrors,
            ContinueOnError = options.continueOnError,
            TargetUrl = options.targetUrl,
            Account = options.account ?? string.Empty,
        };

        // -help takes a topic, and that topic is very often itself a reserved
        // word ("build", "pin"). Capture it before either reserved-word table
        // consumes it as a command.
        if (options.help)
        {
            invocation.HelpTopic =
                invocation.RemainingArguments.FirstOrDefault();
        }

        ApplyNoDashCommand(options, invocation.RemainingArguments);

        if (BarewordVerbs.TryApply(
                options,
                invocation.RemainingArguments,
                out var transpiler))
        {
            invocation.Transpiler = transpiler ?? string.Empty;
            invocation.RawTranspilerArg = transpiler;
        }

        return invocation;
    }

    private static void ApplyNoDashCommand(
        CliOptions options,
        IList<string> remainingArguments)
    {
        if (remainingArguments.Count == 0 ||
            !IsBareword(remainingArguments[0]) ||
            !NoDashCommandForms.TryGetValue(
                remainingArguments[0],
                out var command))
        {
            return;
        }

        switch (command)
        {
            case "listSettings":
                options.listSettings = true;
                break;
            case "addSetting":
                ConsumeListValue(options.addSetting, remainingArguments);
                break;
            case "removeSetting":
                ConsumeListValue(options.removeSetting, remainingArguments);
                break;
            case "execute":
                options.execute = ConsumeStringValue(remainingArguments);
                break;
            case "setAccountAPIKey":
                options.setAccountAPIKey = ConsumeStringValue(remainingArguments);
                break;
            case "pin":
                // "pin <tool> <value>": take the value from the third slot and
                // leave the tool where the resolver expects it.
                if (remainingArguments.Count >= 3)
                {
                    options.pin = remainingArguments[2];
                    remainingArguments.RemoveAt(2);
                }

                break;
            case "compileOnSave":
                options.compileOnSave =
                    ConsumeStringValue(remainingArguments) ?? FindRulebookToWatch;
                break;
            case "buildOnSave":
                options.buildOnSave =
                    ConsumeStringValue(remainingArguments) ?? FindRulebookToWatch;
                break;
            case "rebuildAllOnSave":
                options.rebuildAllOnSave =
                    ConsumeStringValue(remainingArguments) ?? FindRulebookToWatch;
                break;
            case "listVersions":
                options.listVersions = true;
                break;
            case "checkVersion":
                options.checkVersion = true;
                break;
        }

        remainingArguments.RemoveAt(0);
    }

    private static void ConsumeListValue(
        ICollection<string> target,
        IList<string> remainingArguments)
    {
        var value = ConsumeStringValue(remainingArguments);
        if (value is not null)
        {
            target.Add(value);
        }
    }

    private static string ConsumeStringValue(
        IList<string> remainingArguments)
    {
        if (remainingArguments.Count < 2)
        {
            return null;
        }

        var value = remainingArguments[1];
        remainingArguments.RemoveAt(1);
        return value;
    }

    private static string[] NormalizeArguments(IEnumerable<string> arguments)
    {
        var normalized = arguments
            .Select(NormalizeDoubleDashOption)
            .ToArray();

        if (normalized.Length > 0 &&
            IsBareword(normalized[0]) &&
            NoDashCommandForms.TryGetValue(
                normalized[0],
                out var canonicalCommand))
        {
            // D17: "pin <tool> <version|url>" keeps the tool in the tool
            // position and hands the trailing value to the -pin option. With
            // the value missing, fall through so Plossum reports -pin's
            // missing value rather than silently doing nothing.
            if (canonicalCommand == "pin")
            {
                if (normalized.Length < 3)
                {
                    return ["-pin"];
                }

                var reordered = new List<string> { "-pin", normalized[2] };
                reordered.Add(normalized[1]);
                for (var index = 3; index < normalized.Length; index++)
                {
                    reordered.Add(normalized[index]);
                }

                return reordered.ToArray();
            }

            normalized[0] = "-" + canonicalCommand;
        }

        return ResolveSaveWatchFile(normalized);
    }

    /// <summary>
    /// Settles the optional file argument of the save watchers
    /// before Plossum sees a string option standing on its own. Two shapes are
    /// filled in here: the flag with nothing after it (search for the
    /// rulebook), and the flag whose file was typed as an input
    /// (`-compileOnSave -i rules.json`), which means the same as naming the
    /// file positionally and so consumes that input rather than leaving it to
    /// be passed on to a build.
    /// </summary>
    private static string[] ResolveSaveWatchFile(string[] arguments)
    {
        var flagIndex = Array.FindIndex(
            arguments,
            argument => SaveWatchFlags.Contains(argument ?? string.Empty));
        if (flagIndex < 0 ||
            (flagIndex + 1 < arguments.Length &&
             IsBareword(arguments[flagIndex + 1])))
        {
            return arguments;
        }

        var settled = arguments.ToList();
        var file = TakeInputValue(settled) ?? FindRulebookToWatch;

        // Taking the input pair out can shift the flag, so find it again.
        var insertAt = settled.FindIndex(
            argument => SaveWatchFlags.Contains(argument ?? string.Empty));
        settled.Insert(insertAt + 1, file);
        return settled.ToArray();
    }

    private static string TakeInputValue(IList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (!InputFlags.Contains(arguments[index] ?? string.Empty) ||
                !IsBareword(arguments[index + 1]))
            {
                continue;
            }

            var value = arguments[index + 1];
            arguments.RemoveAt(index + 1);
            arguments.RemoveAt(index);
            return value;
        }

        return null;
    }

    private static string NormalizeLeadingNoDashCommand(
        string commandLine)
    {
        var start = 0;
        while (start < commandLine.Length &&
               char.IsWhiteSpace(commandLine[start]))
        {
            start++;
        }

        var end = start;
        while (end < commandLine.Length &&
               !char.IsWhiteSpace(commandLine[end]))
        {
            end++;
        }

        if (start == end)
        {
            return commandLine;
        }

        var candidate = commandLine.Substring(start, end - start);
        if (!IsBareword(candidate) ||
            !NoDashCommandForms.TryGetValue(
                candidate,
                out var canonicalCommand))
        {
            return commandLine;
        }

        return commandLine.Substring(0, start) +
               "-" +
               canonicalCommand +
               commandLine.Substring(end);
    }

    private static string ToPlossumCommandLine(
        IEnumerable<string> arguments)
    {
        return string.Join(
            " ",
            arguments.Select(QuotePlossumArgument));
    }

    private static string QuotePlossumArgument(string argument)
    {
        argument ??= string.Empty;
        if (argument.Length > 0 &&
            !argument.Any(char.IsWhiteSpace) &&
            !argument.Contains('\\') &&
            !argument.Contains('"'))
        {
            return argument;
        }

        return "\"" +
               argument
                   .Replace("\\", "\\\\")
                   .Replace("\"", "\\\"") +
               "\"";
    }

    private static string NormalizeDoubleDashOption(string argument)
    {
        if (string.IsNullOrEmpty(argument) ||
            !argument.StartsWith("--", StringComparison.Ordinal) ||
            argument.Length == 2)
        {
            return argument;
        }

        var assignmentIndex = argument.IndexOfAny(new[] { '=', ':' }, 2);
        var nameLength = assignmentIndex < 0
            ? argument.Length - 2
            : assignmentIndex - 2;
        var optionName = argument.Substring(2, nameLength);

        return KnownOptionNames.Contains(optionName)
            ? argument.Substring(1)
            : argument;
    }

    private static bool IsBareword(string argument)
    {
        return !string.IsNullOrEmpty(argument) &&
               argument[0] != '-' &&
               argument[0] != '/' &&
               argument[0] != '@';
    }

    private static IReadOnlySet<string> GetKnownOptionNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in typeof(CliOptions).GetProperties())
        {
            var attribute =
                property.GetCustomAttribute<CommandLineOptionAttribute>();
            if (attribute is null)
            {
                continue;
            }

            names.Add(property.Name);
            foreach (var alias in (attribute.Aliases ?? string.Empty)
                         .Split(
                             new[] { ',', ';', ' ' },
                             StringSplitOptions.RemoveEmptyEntries))
            {
                names.Add(alias);
            }
        }

        return names;
    }

    private static int GetSafeHelpWidth(int min = 80, int pad = 4)
    {
        try
        {
            if (Console.IsOutputRedirected ||
                Console.IsErrorRedirected ||
                Console.IsInputRedirected)
            {
                return min;
            }

            return Math.Max(Console.WindowWidth - pad, min);
        }
        catch
        {
            return min;
        }
    }

}
