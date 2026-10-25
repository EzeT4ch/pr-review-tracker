using Prt.Core.Domain;

namespace Prt.Web;

/// <summary>
/// The last few PR diffs fetched from Bitbucket, so reloading a diff page does not refetch it.
/// Small and short-lived on purpose; emptied when the server goes to sleep.
/// </summary>
public sealed class DiffCache(TimeProvider clock)
{
    private const int Capacity = 3;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private readonly Lock _lock = new();
    private readonly List<(PrRef Pr, DateTimeOffset At, string Diff)> _entries = [];

    public async Task<(string Diff, DateTimeOffset FetchedAt)> GetAsync(PrRef pr, Func<Task<string>> fetch, bool refresh)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            _entries.RemoveAll(e => now - e.At > Ttl || (refresh && e.Pr == pr));
            if (_entries.FirstOrDefault(e => e.Pr == pr) is { Diff: not null } hit)
                return (hit.Diff, hit.At);
        }

        var diff = await fetch();
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Pr == pr);
            _entries.Insert(0, (pr, now, diff));
            if (_entries.Count > Capacity)
                _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        }
        return (diff, now);
    }

    public void Clear()
    {
        lock (_lock)
            _entries.Clear();
    }
}
