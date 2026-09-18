#nullable enable
namespace Effortless.Cli.Project;

/// <summary>
/// Watches one file and runs an action each time it is saved.
///
/// Two properties matter more than anything else here, and both exist because
/// the action usually WRITES BACK TO THE WATCHED FILE (compile-rulebook upserts
/// the rulebook it just read):
///
/// 1. <b>Never two runs at once.</b> A save that arrives while a run is in
///    flight does not start a second run; it sets a single pending flag. When
///    the current run finishes, at most ONE follow-up run happens, no matter how
///    many saves arrived. A burst of ten saves during a build produces one more
///    build, not ten. This is the whole point of the class.
/// 2. <b>A run's own write-back never retriggers it.</b> Events raised while a
///    run is in flight, and for a short settle window after it finishes, are
///    ignored rather than queued. Without this the tool's own output would
///    trigger the next run forever.
///
/// Coalescing (rather than dropping saves outright) is deliberate: dropping
/// would let an edit made mid-run go silently uncompiled, leaving output that
/// looks current but is stale. Coalescing guarantees the last save on disk is
/// always reflected by the last run.
///
/// Time and delay are injectable so the re-entrancy rules can be tested without
/// real timers or real file systems.
/// </summary>
public sealed class SaveWatcher
{
    private static readonly TimeSpan DefaultDebounce =
        TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long after a run completes that further change events are still
    /// treated as that run's own write-back rather than a new edit. The file
    /// system can raise a write event slightly after the writing process
    /// returns, so the gate has to outlive the run itself.
    /// </summary>
    private static readonly TimeSpan DefaultSettle =
        TimeSpan.FromMilliseconds(400);

    private readonly TimeSpan _debounce;
    private readonly TimeSpan _settle;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action<string> _writeLine;

    private readonly object _gate = new();
    private bool _running;
    private bool _pending;

    public SaveWatcher(
        TimeSpan? debounce = null,
        TimeSpan? settle = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<string>? writeLine = null)
    {
        _debounce = debounce ?? DefaultDebounce;
        _settle = settle ?? DefaultSettle;
        _delayAsync = delayAsync ?? ((delay, token) => Task.Delay(delay, token));
        _writeLine = writeLine ?? Console.WriteLine;
    }

    /// <summary>
    /// Number of times the action actually ran. Exposed for tests asserting the
    /// coalescing rule (N saves during one run must yield exactly one re-run).
    /// </summary>
    public int RunCount { get; private set; }

    /// <summary>
    /// Records a save. Returns true if this save started a run, false if it was
    /// coalesced into an already-running one or ignored as a self-write.
    ///
    /// This is the entire re-entrancy guard, kept in one place and independent
    /// of FileSystemWatcher so it can be tested directly.
    /// </summary>
    public async Task<bool> OnSavedAsync(
        Func<CancellationToken, Task> runAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runAsync);

        lock (_gate)
        {
            if (_running)
            {
                // A run is already in flight. Do not start a second one; just
                // remember that the file changed again. Setting an existing
                // flag is what collapses a burst into a single re-run.
                _pending = true;
                return false;
            }

            _running = true;
        }

        try
        {
            // Loop rather than recurse: each iteration handles one run, and a
            // save that arrived during it becomes the next iteration. The
            // _running flag stays true for the whole loop, so nothing else can
            // start a concurrent run while we drain.
            while (true)
            {
                await _delayAsync(_debounce, cancellationToken);

                RunCount++;
                try
                {
                    await runAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A failure must not kill the watcher: the usual cause is a
                    // typo in a formula, and the user's next save is the fix.
                    _writeLine(ex.Message);
                    _writeLine("Still watching. Save again to retry.");
                }

                // Ignore the write-back this run just produced.
                await _delayAsync(_settle, cancellationToken);

                lock (_gate)
                {
                    if (!_pending)
                    {
                        _running = false;
                        return true;
                    }

                    // Exactly one more pass, regardless of how many saves set
                    // the flag while we were busy.
                    _pending = false;
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                _running = false;
                _pending = false;
            }

            throw;
        }
    }

    /// <summary>
    /// Watches <paramref name="filePath"/> until cancelled, running
    /// <paramref name="runAsync"/> on each save under the rules above.
    /// </summary>
    public Task WatchAsync(
        string filePath,
        Func<CancellationToken, Task> runAsync,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException(
                "A file to watch is required.",
                nameof(filePath));
        }

        return WatchAsync(new[] { filePath }, runAsync, cancellationToken);
    }

    /// <summary>
    /// Watches every file in <paramref name="filePaths"/> until cancelled,
    /// running <paramref name="runAsync"/> once per save under the rules
    /// above, no matter which watched file changed. A save to any file counts
    /// as one save through the same coalescing guard as the single-file
    /// overload: a burst across several files during a run still produces at
    /// most one follow-up run, not one per file.
    /// </summary>
    public async Task WatchAsync(
        IReadOnlyList<string> filePaths,
        Func<CancellationToken, Task> runAsync,
        CancellationToken cancellationToken = default)
    {
        if (filePaths is null || filePaths.Count == 0)
        {
            throw new ArgumentException(
                "At least one file to watch is required.",
                nameof(filePaths));
        }

        ArgumentNullException.ThrowIfNull(runAsync);

        var fileInfos = filePaths.Select(path => new FileInfo(path)).ToList();
        var missing = fileInfos.FirstOrDefault(f => !f.Exists);
        if (missing is not null)
        {
            throw new FileNotFoundException(
                $"Cannot watch '{missing.FullName}': the file does not exist.",
                missing.FullName);
        }

        var saved = new Channel();
        var watchers = fileInfos
            .Select(fileInfo =>
            {
                var fsWatcher = new FileSystemWatcher(
                    fileInfo.Directory!.FullName,
                    fileInfo.Name)
                {
                    NotifyFilter = NotifyFilters.LastWrite
                        | NotifyFilters.Size
                        | NotifyFilters.FileName,
                };

                // Editors frequently save by writing a temp file and renaming
                // it over the target, which surfaces as Created/Renamed
                // rather than Changed.
                fsWatcher.Changed += (_, _) => saved.Signal();
                fsWatcher.Created += (_, _) => saved.Signal();
                fsWatcher.Renamed += (_, _) => saved.Signal();
                fsWatcher.EnableRaisingEvents = true;
                return fsWatcher;
            })
            .ToList();

        try
        {
            foreach (var fileInfo in fileInfos)
            {
                _writeLine($"Watching {fileInfo.FullName}");
            }

            _writeLine("Save a watched file to recompile. Ctrl+C to stop.");

            while (!cancellationToken.IsCancellationRequested)
            {
                await saved.WaitAsync(cancellationToken);
                await OnSavedAsync(runAsync, cancellationToken);
            }
        }
        finally
        {
            foreach (var fsWatcher in watchers)
            {
                fsWatcher.Dispose();
            }
        }
    }

    /// <summary>
    /// A one-slot signal: many events collapse into a single wake-up, which is
    /// the coalescing rule applied at the event level too.
    /// </summary>
    private sealed class Channel
    {
        private readonly object _lock = new();
        private TaskCompletionSource _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Signal()
        {
            lock (_lock)
            {
                _tcs.TrySetResult();
            }
        }

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            Task task;
            lock (_lock)
            {
                task = _tcs.Task;
            }

            await task.WaitAsync(cancellationToken);

            lock (_lock)
            {
                if (_tcs.Task.IsCompleted)
                {
                    _tcs = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
    }
}
