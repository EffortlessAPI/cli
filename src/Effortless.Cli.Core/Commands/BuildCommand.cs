#nullable enable
using Effortless.Cli.Options;
using Effortless.Cli.Project;

namespace Effortless.Cli.Commands;

public sealed class BuildCommand
{
    private readonly Func<string, EffortlessProject, bool, BuildErrorLog, int>
        _runCommandLine;
    private readonly TriggerBuildWatcher _triggerWatcher;

    public BuildCommand(
        Func<string, EffortlessProject, bool, BuildErrorLog, int> runCommandLine,
        TriggerBuildWatcher? triggerWatcher = null)
    {
        _runCommandLine = runCommandLine;
        _triggerWatcher =
            triggerWatcher ?? new TriggerBuildWatcher();
    }

    public int Run(
        CliInvocation invocation,
        bool all,
        bool withSubprojects = false)
    {
        // -compileOnSave and -buildOnSave are the same watcher over the same
        // file; they differ only in what runs on each save. compileOnSave runs
        // compile-rulebook against that one file (the authoring inner loop);
        // buildOnSave runs the whole build (downstream artifacts follow too).
        var compileOnSave = invocation.Options.compileOnSave;
        var buildOnSave = invocation.Options.buildOnSave;
        if (!string.IsNullOrWhiteSpace(compileOnSave)
            || !string.IsNullOrWhiteSpace(buildOnSave))
        {
            return RunOnSave(
                invocation,
                all,
                withSubprojects,
                !string.IsNullOrWhiteSpace(compileOnSave)
                    ? compileOnSave
                    : buildOnSave,
                compileOnly: !string.IsNullOrWhiteSpace(compileOnSave));
        }

        if (string.IsNullOrWhiteSpace(
                invocation.Options.buildOnTrigger))
        {
            return RunOnce(invocation, all, withSubprojects);
        }

        _triggerWatcher.WatchAsync(
                invocation.Options.buildOnTrigger,
                () =>
                {
                    var result = RunOnce(
                        invocation,
                        all,
                        withSubprojects);
                    if (result != 0)
                    {
                        throw new InvalidOperationException(
                            $"Triggered build exited with code {result}.");
                    }

                    return Task.CompletedTask;
                })
            .GetAwaiter()
            .GetResult();
        return 0;
    }

    /// <summary>
    /// Watches one file and recompiles/rebuilds on each save until Ctrl+C.
    /// The re-entrancy rules (never two runs at once, a burst of saves
    /// coalescing into exactly one follow-up run, self-writes ignored) all live
    /// in <see cref="SaveWatcher"/>; this method only supplies the action.
    /// </summary>
    private int RunOnSave(
        CliInvocation invocation,
        bool all,
        bool withSubprojects,
        string fileToWatch,
        bool compileOnly)
    {
        var project = invocation.Project!;

        // A relative path means what it means in the shell: relative to where
        // the user actually is. Running `-compileOnSave effortless-rulebook.json`
        // from inside effortless-rulebook/ must watch the file right there, so
        // the current directory is tried first. The project root is only a
        // fallback, which keeps the from-the-root form
        // (`-compileOnSave effortless-rulebook/effortless-rulebook.json`)
        // working no matter which folder the CLI was invoked from.
        var fileInfo = new FileInfo(
            Path.IsPathRooted(fileToWatch)
                ? fileToWatch
                : Path.Combine(Environment.CurrentDirectory, fileToWatch));
        if (!fileInfo.Exists && !Path.IsPathRooted(fileToWatch))
        {
            var fromProjectRoot = new FileInfo(
                Path.Combine(project.RootPath, fileToWatch));
            if (fromProjectRoot.Exists)
            {
                fileInfo = fromProjectRoot;
            }
        }

        if (!fileInfo.Exists)
        {
            CliLog.LogLine(
                $"Cannot watch '{fileInfo.FullName}': the file does not exist.",
                ConsoleColor.Red);
            return 1;
        }

        var watcher = new SaveWatcher(writeLine: line => CliLog.LogLine(line));
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            watcher.WatchAsync(
                    fileInfo.FullName,
                    _ =>
                    {
                        int result;
                        if (compileOnly)
                        {
                            // Just this one file through compile-rulebook, not
                            // the project's whole transpiler chain.
                            //
                            // The input must be named RELATIVE to the file's own
                            // directory, and run from there: compile-rulebook
                            // writes its result back to the same relative path it
                            // was given, so an absolute -i would land a duplicate
                            // at the project root instead of upserting in place.
                            // BuildRunner runs each step from its RelativePath by
                            // setting Environment.CurrentDirectory; do the same so
                            // a watched file behaves exactly like a registered
                            // compile-rulebook step.
                            var originalDirectory = Environment.CurrentDirectory;
                            try
                            {
                                Environment.CurrentDirectory =
                                    fileInfo.Directory!.FullName;
                                result = _runCommandLine(
                                    $"compile-rulebook -i {fileInfo.Name}",
                                    project,
                                    invocation.Options.continueOnError,
                                    invocation.BuildErrorLog);
                            }
                            finally
                            {
                                Environment.CurrentDirectory = originalDirectory;
                            }
                        }
                        else
                        {
                            result = RunOnce(invocation, all, withSubprojects);
                        }

                        if (result != 0)
                        {
                            throw new InvalidOperationException(
                                compileOnly
                                    ? $"Compile exited with code {result}."
                                    : $"Build exited with code {result}.");
                        }

                        return Task.CompletedTask;
                    },
                    cts.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is the documented way to stop watching, not a failure.
        }

        return 0;
    }

    private int RunOnce(
        CliInvocation invocation,
        bool all,
        bool withSubprojects)
    {
        var project = invocation.Project!;
        var command = withSubprojects
            ? "buildWithSubprojects"
            : all
                ? "buildAll"
                : invocation.Options.buildLocal
                    ? "build -buildLocal"
                    : "build";
        invocation.BuildErrorLog.Begin(
            project.RootPath,
            invocation.Options.continueOnError,
            command);
        try
        {
            // A build that matches no steps otherwise prints nothing at all and
            // exits zero, which reads as a broken CLI rather than an empty
            // project. `-init` sets build itself, so only say this when the user
            // actually asked for a build.
            if (!invocation.Options.init
                && (project.ProjectTranspilers is null
                    || project.ProjectTranspilers.Count == 0))
            {
                CliLog.LogLine(
                    "Nothing to build: no transpiler steps are registered in "
                    + "effortless.json.",
                    ConsoleColor.Yellow);
                CliLog.LogLine(
                    "Add one with: effortless -install <tool-name>",
                    ConsoleColor.Yellow);
            }

            var runner = new BuildRunner(
                project,
                _runCommandLine,
                invocation.BuildErrorLog);
            if (all)
            {
                runner.RebuildAll(
                    project.RootPath,
                    invocation.Options.includeDisabled,
                    invocation.Options.transpilerGroup,
                    invocation.Options.buildLocal,
                    invocation.Options.debug,
                    invocation.Options.continueOnError,
                    withSubprojects);
            }
            else
            {
                runner.Rebuild(
                    invocation.CurrentDirectory!,
                    invocation.Options.includeDisabled,
                    invocation.Options.transpilerGroup,
                    invocation.Options.buildLocal,
                    invocation.Options.debug,
                    continueOnError:
                    invocation.Options.continueOnError);
            }

            return 0;
        }
        finally
        {
            invocation.BuildErrorLog.Finish();
        }
    }
}
