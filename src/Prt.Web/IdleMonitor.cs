using System.Runtime;
using Microsoft.Data.Sqlite;

namespace Prt.Web;

/// <summary>
/// Puts the server to sleep after a period without requests: closes pooled SQLite connections and
/// returns memory to the OS. While asleep nothing runs (no timer); the next request wakes it up.
/// </summary>
public sealed class IdleMonitor(TimeSpan sleepAfter, Func<bool> busy, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private DateTimeOffset _lastActivity = (clock ?? TimeProvider.System).GetUtcNow();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Asleep { get; private set; }
    public int Sleeps { get; private set; }
    public DateTimeOffset LastActivity { get { lock (_lock) return _lastActivity; } }

    /// <summary>Called on every request.</summary>
    public void Touch()
    {
        lock (_lock)
        {
            _lastActivity = _clock.GetUtcNow();
            if (!Asleep)
                return;
            Asleep = false;
            _wake.TrySetResult();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (sleepAfter <= TimeSpan.Zero)
            return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var remaining = LastActivity + sleepAfter - _clock.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, _clock, ct);
                    continue;
                }
                if (busy())
                {
                    // A background job is running: check again later instead of sleeping under it.
                    await Task.Delay(TimeSpan.FromMinutes(1), _clock, ct);
                    continue;
                }

                Task wake;
                lock (_lock)
                {
                    if (_clock.GetUtcNow() - _lastActivity < sleepAfter)
                        continue;
                    Asleep = true;
                    Sleeps++;
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wake = _wake.Task;
                }
                Trim();
                await wake.WaitAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Server stopping.
        }
    }

    internal static void Trim()
    {
        SqliteConnection.ClearAllPools();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
}
