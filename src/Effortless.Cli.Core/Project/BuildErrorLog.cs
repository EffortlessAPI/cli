#nullable enable
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Effortless.Cli;

namespace Effortless.Cli.Project;

public class TranspilerStepFailedException : Exception
{
    public TranspilerStepFailedException(string message)
        : base(message)
    {
    }

    public TranspilerStepFailedException(
        string message,
        Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A seed license bound to this project has been revoked (or is for the
/// wrong seed), per the recheck BuildRunner makes before any transpiler
/// runs. This is the one thing a seed license check gates on today -- an
/// available, bound-elsewhere, or expired key is logged, not fatal.
/// </summary>
public class SeedLicenseRevokedException : Exception
{
    public SeedLicenseRevokedException(string message)
        : base(message)
    {
    }
}

public sealed class BuildErrorLog
{
    public const string ErrorsFileName = "errors.json";
    public const string SchemaId = "effortless-build-errors/v1";

    private bool _active;
    private string? _projectRoot;
    private bool _continueOnError;
    private string? _buildCommand;
    private DateTime _startedAtUtc;
    private readonly List<StepRecord> _steps =
        new List<StepRecord>();

    private static readonly JsonSerializerSettings CamelCase =
        new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver =
                new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

    public bool IsActive
    {
        get { return _active; }
    }

    public bool HasFailures
    {
        get { return _steps.Any(s => s.Status == "failed"); }
    }

    public int FailureCount
    {
        get { return _steps.Count(s => s.Status == "failed"); }
    }

    public void Begin(
        string projectRoot,
        bool continueOnError,
        string buildCommand)
    {
        _active = true;
        _projectRoot = projectRoot;
        _continueOnError = continueOnError;
        _buildCommand = buildCommand;
        _startedAtUtc = DateTime.UtcNow;
        _steps.Clear();
    }

    public void RecordSuccess(ProjectTranspiler projectTranspiler)
    {
        if (!_active || ReferenceEquals(projectTranspiler, null))
        {
            return;
        }

        _steps.Add(new StepRecord
        {
            Name = projectTranspiler.Name,
            RelativePath = projectTranspiler.RelativePath,
            CommandLine = projectTranspiler.CommandLine,
            Status = "succeeded",
            FinishedAt = DateTime.UtcNow.ToString("o"),
        });
    }

    public void RecordSkipped(
        ProjectTranspiler projectTranspiler,
        string reason)
    {
        if (!_active || ReferenceEquals(projectTranspiler, null))
        {
            return;
        }

        _steps.Add(new StepRecord
        {
            Name = projectTranspiler.Name,
            RelativePath = projectTranspiler.RelativePath,
            CommandLine = projectTranspiler.CommandLine,
            Status = "skipped",
            Message = reason,
            FinishedAt = DateTime.UtcNow.ToString("o"),
        });
    }

    public void RecordFailure(
        ProjectTranspiler? projectTranspiler,
        int exitCode,
        Exception? transpilerException,
        Exception? thrownException,
        string? resolvedVersion,
        string? resolvedUrl)
    {
        if (!_active)
        {
            return;
        }

        var message = FirstNonBlank(
            transpilerException?.Message,
            thrownException?.Message,
            exitCode != 0
                ? $"the tool exited with code {exitCode} but reported no error message"
                : null,
            "the tool failed but reported no error message");

        _steps.Add(new StepRecord
        {
            Name = projectTranspiler?.Name,
            RelativePath = projectTranspiler?.RelativePath,
            CommandLine = projectTranspiler?.CommandLine,
            Status = "failed",
            ExitCode = exitCode,
            Message = message,
            ResolvedVersion = resolvedVersion,
            ResolvedUrl = resolvedUrl,
            FinishedAt = DateTime.UtcNow.ToString("o"),
            TranspilerException = Describe(transpilerException),
            CliException = Describe(thrownException),
        });
    }

    public string? Finish()
    {
        if (!_active)
        {
            return null;
        }

        _active = false;

        var failures = _steps
            .Where(s => s.Status == "failed")
            .ToList();
        var errorsPath = String.IsNullOrEmpty(_projectRoot)
            ? null
            : Path.Combine(_projectRoot, ErrorsFileName);

        if (!failures.Any())
        {
            if (errorsPath != null && File.Exists(errorsPath))
            {
                try
                {
                    File.Delete(errorsPath);
                    CliLog.LogLine(
                        $"build clean — removed stale {ErrorsFileName}");
                }
                catch (Exception ex)
                {
                    CliLog.LogLine(
                        $"WARNING: could not remove stale {errorsPath}: {ex.Message}",
                        ConsoleColor.Yellow);
                }
            }

            return null;
        }

        var report = new
        {
            schema = SchemaId,
            generatedAt = DateTime.UtcNow.ToString("o"),
            startedAt = _startedAtUtc.ToString("o"),
            projectRoot = _projectRoot,
            buildCommand = _buildCommand,
            continueOnError = _continueOnError,
            totalSteps = _steps.Count,
            succeededSteps =
                _steps.Count(s => s.Status == "succeeded"),
            failedSteps = failures.Count,
            skippedSteps =
                _steps.Count(s => s.Status == "skipped"),
            failedStepNames =
                failures.Select(f => f.Name).ToList(),
            steps = _steps.Select(s => new
            {
                name = s.Name,
                relativePath = s.RelativePath,
                status = s.Status,
                message = s.Message,
                finishedAt = s.FinishedAt,
            }).ToList(),
            errors = failures,
        };

        PrintSummary(failures, errorsPath);

        if (errorsPath == null)
        {
            CliLog.LogLine(
                $"(no project root resolved — {ErrorsFileName} was not written)",
                ConsoleColor.Yellow);
            return null;
        }

        try
        {
            File.WriteAllText(
                errorsPath,
                JsonConvert.SerializeObject(report, CamelCase));
            return errorsPath;
        }
        catch (Exception ex)
        {
            CliLog.LogLine(
                $"WARNING: could not write {errorsPath}: {ex.Message}",
                ConsoleColor.Yellow);
            return null;
        }
    }

    private void PrintSummary(
        List<StepRecord> failures,
        string? errorsPath)
    {
        var succeeded =
            _steps.Count(s => s.Status == "succeeded");
        Console.WriteLine();
        CliLog.LogLine(
            "============================================================",
            ConsoleColor.Yellow);
        CliLog.LogLine(
            _continueOnError
                ? $"BUILD FINISHED WITH {failures.Count} FAILED STEP(S) — {succeeded} step(s) succeeded and are usable."
                : $"BUILD FAILED — {failures.Count} failed step(s), {succeeded} succeeded.",
            ConsoleColor.Yellow);
        CliLog.LogLine(
            "============================================================",
            ConsoleColor.Yellow);

        foreach (var failure in failures)
        {
            CliLog.LogLine(
                $"  FAILED: {failure.Name}  ({failure.RelativePath})",
                ConsoleColor.Red);
            CliLog.LogLine(
                $"    command: effortless {failure.CommandLine}",
                ConsoleColor.Red);
            CliLog.LogLine(
                $"    error:   {failure.Message}",
                ConsoleColor.Red);
        }

        if (!String.IsNullOrEmpty(errorsPath))
        {
            CliLog.LogLine(
                $"Full exception detail (stack traces, inner exceptions): {errorsPath}",
                ConsoleColor.Cyan);
        }

        if (_continueOnError)
        {
            CliLog.LogLine(
                "-continueOnError is set, so the build ran every remaining step and is reporting success.",
                ConsoleColor.Cyan);
        }

        Console.WriteLine();
    }

    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!String.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static ExceptionRecord? Describe(Exception? exception)
    {
        if (ReferenceEquals(exception, null))
        {
            return null;
        }

        return new ExceptionRecord
        {
            Type = exception.GetType().FullName,
            Message = String.IsNullOrWhiteSpace(exception.Message)
                ? "(the exception carried an empty message)"
                : exception.Message,
            StackTrace = exception.StackTrace,
            Inner = Describe(exception.InnerException),
        };
    }

    public class StepRecord
    {
        public string? Name { get; set; }
        public string? RelativePath { get; set; }
        public string? CommandLine { get; set; }
        public string? Status { get; set; }
        public int? ExitCode { get; set; }
        public string? Message { get; set; }
        public string? ResolvedVersion { get; set; }
        public string? ResolvedUrl { get; set; }
        public string? FinishedAt { get; set; }
        public ExceptionRecord? TranspilerException { get; set; }
        public ExceptionRecord? CliException { get; set; }
    }

    public class ExceptionRecord
    {
        public string? Type { get; set; }
        public string? Message { get; set; }
        public string? StackTrace { get; set; }
        public ExceptionRecord? Inner { get; set; }
    }
}
