namespace Planner.Endpoints.Health;

/// <summary>Per-host, per-tier single-flight snapshot cache. Request cancellation never cancels a shared probe.</summary>
public sealed class HealthStatusCache<T> where T : class
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _utcNow;

    public HealthStatusCache(TimeSpan ttl, Func<DateTimeOffset>? utcNow = null)
    {
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        _ttl = ttl;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<T> GetAsync(string key, Func<Task<T>> refresh, CancellationToken callerCancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(refresh);

        Task<T> shared;
        Entry? start = null;
        lock (_gate)
        {
            var now = _utcNow();
            if (_entries.TryGetValue(key, out var cached) &&
                (cached.Refreshing || cached.ExpiresAt > now))
            {
                shared = cached.Completion.Task;
            }
            else
            {
                start = new Entry { Refreshing = true };
                shared = start.Completion.Task;
                _entries[key] = start;
            }
        }

        if (start is not null) _ = RefreshAndCacheAsync(key, start, refresh);
        return await shared.WaitAsync(callerCancellation).ConfigureAwait(false);
    }

    private async Task RefreshAndCacheAsync(string key, Entry entry, Func<Task<T>> refresh)
    {
        try { entry.Completion.TrySetResult(await refresh().ConfigureAwait(false)); }
        catch (Exception ex) { entry.Completion.TrySetException(ex); }
        finally
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    entry.Refreshing = false;
                    entry.ExpiresAt = _utcNow() + _ttl;
                }
            }
        }
    }

    private sealed class Entry
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset ExpiresAt { get; set; }
        public bool Refreshing { get; set; }
    }
}

public static class HealthStatusCacheAccessor
{
    public static HealthStatusCache<HealthStatusSnapshot> Instance { get; } =
        new(TimeSpan.FromSeconds(5));
}
