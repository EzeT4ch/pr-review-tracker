using Prt.Core.Domain;

namespace Prt.Web;

/// <summary>
/// The last few things fetched from Bitbucket for a PR (its diff, its comments), so reloading a page does not
/// refetch them. Small and short-lived on purpose; emptied when the server goes to sleep.
/// </summary>
public sealed class PrCache<T>(TimeProvider clock) where T : class
{
    private const int Capacity = 3;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private readonly Lock _lock = new();
    private readonly List<(PrRef Pr, DateTimeOffset At, T Value)> _entries = [];

    public async Task<(T Value, DateTimeOffset FetchedAt)> GetAsync(PrRef pr, Func<Task<T>> fetch, bool refresh)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            _entries.RemoveAll(e => now - e.At > Ttl || (refresh && e.Pr == pr));
            if (_entries.FirstOrDefault(e => e.Pr == pr) is { Value: not null } hit)
                return (hit.Value, hit.At);
        }

        var value = await fetch();
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Pr == pr);
            _entries.Insert(0, (pr, now, value));
            if (_entries.Count > Capacity)
                _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        }
        return (value, now);
    }

    /// <summary>Forgets what was fetched for the PR (after changing it).</summary>
    public void Invalidate(PrRef pr)
    {
        lock (_lock)
            _entries.RemoveAll(e => e.Pr == pr);
    }

    public void Clear()
    {
        lock (_lock)
            _entries.Clear();
    }
}
