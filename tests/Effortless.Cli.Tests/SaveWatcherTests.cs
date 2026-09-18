using Effortless.Cli.Project;

namespace Effortless.Cli.Tests;

/// <summary>
/// The re-entrancy rules are the reason SaveWatcher exists, so they are tested
/// directly against the guard rather than through a real FileSystemWatcher:
/// timing-dependent file-system tests would prove the rules far less reliably
/// than driving the guard itself.
/// </summary>
public sealed class SaveWatcherTests
{
    private static SaveWatcher NoDelayWatcher(Action<string>? writeLine = null)
        => new(
            debounce: TimeSpan.Zero,
            settle: TimeSpan.Zero,
            delayAsync: (_, _) => Task.CompletedTask,
            writeLine: writeLine ?? (_ => { }));

    [Fact(DisplayName = "unit-save-watcher-never-concurrent: a save during a run never starts a second run")]
    public async Task SaveDuringRunNeverStartsSecondRun()
    {
        var watcher = NoDelayWatcher();
        var concurrent = 0;
        var maxConcurrent = 0;
        var gate = new object();
        var release = new TaskCompletionSource();

        Task Run(CancellationToken _)
        {
            lock (gate)
            {
                concurrent++;
                maxConcurrent = Math.Max(maxConcurrent, concurrent);
            }

            return release.Task.ContinueWith(
                _ =>
                {
                    lock (gate)
                    {
                        concurrent--;
                    }
                },
                TaskScheduler.Default);
        }

        var first = watcher.OnSavedAsync(Run);

        // Saves that land while the first run is still in flight.
        var second = await watcher.OnSavedAsync(Run);
        var third = await watcher.OnSavedAsync(Run);

        release.SetResult();
        await first;

        Assert.False(second, "a save during a run must not start its own run");
        Assert.False(third, "a save during a run must not start its own run");
        Assert.Equal(1, maxConcurrent);
    }

    [Fact(DisplayName = "unit-save-watcher-coalesces: many saves during one run produce exactly one follow-up run")]
    public async Task ManySavesDuringOneRunProduceExactlyOneFollowUp()
    {
        var watcher = NoDelayWatcher();
        var release = new TaskCompletionSource();
        var started = 0;

        Task Run(CancellationToken _)
        {
            // Only the first run blocks; the coalesced follow-up returns at once.
            return Interlocked.Increment(ref started) == 1
                ? release.Task
                : Task.CompletedTask;
        }

        var first = watcher.OnSavedAsync(Run);

        for (var i = 0; i < 10; i++)
        {
            await watcher.OnSavedAsync(Run);
        }

        release.SetResult();
        await first;

        // Ten saves during one run collapse to a single re-run, not ten.
        Assert.Equal(2, watcher.RunCount);
    }

    [Fact(DisplayName = "unit-save-watcher-survives-failure: a failed run is reported and watching continues")]
    public async Task FailedRunIsReportedAndWatchingContinues()
    {
        var lines = new List<string>();
        var watcher = NoDelayWatcher(lines.Add);
        var attempts = 0;

        Task Run(CancellationToken _)
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("compile failed: bad formula");
            }

            return Task.CompletedTask;
        }

        // The failing save must not throw out of the watcher...
        var firstHandled = await watcher.OnSavedAsync(Run);
        // ...and the next save must still be served.
        var secondHandled = await watcher.OnSavedAsync(Run);

        Assert.True(firstHandled);
        Assert.True(secondHandled);
        Assert.Equal(2, attempts);
        Assert.Contains(lines, l => l.Contains("bad formula"));
        Assert.Contains(lines, l => l.Contains("Still watching"));
        // CliLog adds the "[cli] " prefix; messages must not carry their own.
        Assert.DoesNotContain(lines, l => l.Contains("[cli]"));
    }

    [Fact(DisplayName = "unit-save-watcher-sequential: saves after a run completes each get their own run")]
    public async Task SavesAfterCompletionEachGetTheirOwnRun()
    {
        var watcher = NoDelayWatcher();

        await watcher.OnSavedAsync(_ => Task.CompletedTask);
        await watcher.OnSavedAsync(_ => Task.CompletedTask);
        await watcher.OnSavedAsync(_ => Task.CompletedTask);

        // Coalescing must not suppress genuinely separate edits.
        Assert.Equal(3, watcher.RunCount);
    }

    [Fact(DisplayName = "unit-build-on-save-shares-guard: buildOnSave uses the same re-entrancy guard")]
    public async Task BuildOnSaveUsesTheSameReentrancyGuard()
    {
        // -compileOnSave and -buildOnSave differ only in the action they invoke;
        // both route through this one guard, so the "never twice at once" and
        // coalescing guarantees are identical for a full build.
        var watcher = NoDelayWatcher();
        var release = new TaskCompletionSource();
        var started = 0;
        var concurrent = 0;
        var maxConcurrent = 0;
        var gate = new object();

        async Task Build(CancellationToken _)
        {
            lock (gate)
            {
                concurrent++;
                maxConcurrent = Math.Max(maxConcurrent, concurrent);
            }

            try
            {
                if (Interlocked.Increment(ref started) == 1)
                {
                    await release.Task;
                }
            }
            finally
            {
                lock (gate)
                {
                    concurrent--;
                }
            }
        }

        var first = watcher.OnSavedAsync(Build);
        for (var i = 0; i < 5; i++)
        {
            Assert.False(await watcher.OnSavedAsync(Build));
        }

        release.SetResult();
        await first;

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(2, watcher.RunCount);
    }

    [Fact(DisplayName = "unit-save-watcher-missing-file: watching a file that does not exist fails clearly")]
    public async Task WatchingMissingFileFailsClearly()
    {
        using var directory = new TestDirectory();
        var watcher = NoDelayWatcher();

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => watcher.WatchAsync(
                Path.Combine(directory.Path, "nope.json"),
                _ => Task.CompletedTask));

        Assert.Contains("does not exist", ex.Message);
    }

    [Fact(DisplayName = "unit-save-watcher-multi-file: a save to any watched file triggers a run")]
    public async Task SaveToAnyWatchedFileTriggersRun()
    {
        using var directory = new TestDirectory();
        var rulebookPath = Path.Combine(directory.Path, "effortless-rulebook.json");
        var effortlessJsonPath = Path.Combine(directory.Path, "effortless.json");
        File.WriteAllText(rulebookPath, "{}");
        File.WriteAllText(effortlessJsonPath, "{}");

        var watcher = NoDelayWatcher();
        using var cts = new CancellationTokenSource();
        var runs = 0;
        var ranOnce = new TaskCompletionSource();

        var watchTask = watcher.WatchAsync(
            new[] { rulebookPath, effortlessJsonPath },
            _ =>
            {
                Interlocked.Increment(ref runs);
                ranOnce.TrySetResult();
                return Task.CompletedTask;
            },
            cts.Token);

        // Give the FileSystemWatcher a moment to start, then save the second
        // (non-rulebook) file — it alone must be enough to trigger a run.
        await Task.Delay(200);
        File.WriteAllText(effortlessJsonPath, "{\"changed\":true}");

        await ranOnce.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        try
        {
            await watchTask;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(runs >= 1);
    }

    [Fact(DisplayName = "unit-save-watcher-multi-file-missing: any missing watched file fails clearly")]
    public async Task AnyMissingWatchedFileFailsClearly()
    {
        using var directory = new TestDirectory();
        var existing = Path.Combine(directory.Path, "effortless-rulebook.json");
        File.WriteAllText(existing, "{}");
        var missing = Path.Combine(directory.Path, "effortless.json");

        var watcher = NoDelayWatcher();

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => watcher.WatchAsync(
                new[] { existing, missing },
                _ => Task.CompletedTask));

        Assert.Contains("does not exist", ex.Message);
    }
}
