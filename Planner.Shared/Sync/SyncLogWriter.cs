namespace Planner.Shared.Sync;

/// <summary>
/// Best-effort fase-schrijver. Hij bewaart alleen allowlisted statuscodes en aantallen; foutteksten
/// en exception-objecten worden nooit aan de persistence-delegate aangeboden.
/// </summary>
public sealed class SyncLogWriter
{
    private const int ProgressWriteEvery = 10;
    private static readonly TimeSpan ProgressWriteInterval = TimeSpan.FromSeconds(2);

    private int _incompletePhases;

    /// <summary>Ook als persistence faalt blijft de gemeten onvolledige uitkomst bekend.</summary>
    public bool HasIncompletePhases => Volatile.Read(ref _incompletePhases) != 0;

    private readonly Guid _jobId;
    private readonly int _attempt;
    private readonly Func<SyncLogEntry, Task> _upsert;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PhaseState> _phases = new(StringComparer.Ordinal);

    public SyncLogWriter(Guid jobId, int attempt, Func<SyncLogEntry, Task> upsert, TimeProvider? timeProvider = null)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Sync-logboek vereist een job-id.", nameof(jobId));
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt));
        _jobId = jobId;
        _attempt = attempt;
        _upsert = upsert ?? throw new ArgumentNullException(nameof(upsert));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task BeginPhaseAsync(string phase, DateTime startedAtUtc)
    {
        try
        {
            ValidatePhase(phase);
            SyncLogPolicy.RequireUtc(startedAtUtc, nameof(startedAtUtc));
            await _gate.WaitAsync();
            try
            {
                if (_phases.ContainsKey(phase))
                    return;
                var state = new PhaseState(startedAtUtc, _timeProvider.GetUtcNow().UtcDateTime);
                _phases.Add(phase, state);
                await TryUpsertAsync(Entry(phase, state, null, SyncLogPolicy.StatusRunning, null));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // Aanvullende logging mag de sync-uitkomst nooit veranderen.
        }
    }

    public async Task ReportProgressAsync(
        string phase,
        int? fetched = null,
        int? added = null,
        int? updated = null,
        int? failed = null,
        int? progress = null,
        int? total = null)
    {
        try
        {
            ValidatePhase(phase);
            ValidateCounts(fetched, added, updated, failed, progress, total);
            await _gate.WaitAsync();
            try
            {
                if (!_phases.TryGetValue(phase, out var state) || state.CompletedAt is not null)
                    return;
                var previous = state.Last;
                var merged = new Counters(
                    fetched ?? previous?.Fetched, added ?? previous?.Added,
                    updated ?? previous?.Updated, failed ?? previous?.Failed,
                    progress ?? previous?.Progress, total ?? previous?.Total);
                ValidateCounts(merged.Fetched, merged.Added, merged.Updated, merged.Failed, merged.Progress, merged.Total);
                state.Last = merged;
                state.ProgressCalls++;
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                if (state.ProgressCalls % ProgressWriteEvery != 0 && now - state.LastWrittenAt < ProgressWriteInterval)
                    return;

                state.LastWrittenAt = now;
                await TryUpsertAsync(Entry(phase, state, null, SyncLogPolicy.StatusRunning, null));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // Een ongeldige of te late voortgangsmelding is best-effort.
        }
    }

    public async Task CompletePhaseAsync(
        string phase,
        DateTime completedAtUtc,
        string status,
        int? fetched = null,
        int? added = null,
        int? updated = null,
        int? failed = null,
        string? reasonCode = null)
    {
        try
        {
            ValidatePhase(phase);
            if (!SyncLogPolicy.IsStatus(status) || status is SyncLogPolicy.StatusPending or SyncLogPolicy.StatusRunning)
                return;
            if (!SyncLogPolicy.IsReasonCode(reasonCode))
                return;
            SyncLogPolicy.RequireUtc(completedAtUtc, nameof(completedAtUtc));
            ValidateCounts(fetched, added, updated, failed, null, null);
            await _gate.WaitAsync();
            try
            {
                if (!_phases.TryGetValue(phase, out var state) || state.CompletedAt is not null || completedAtUtc < state.StartedAt)
                    return;
                var previous = state.Last;
                state.Last = new Counters(
                    fetched ?? previous?.Fetched, added ?? previous?.Added,
                    updated ?? previous?.Updated, failed ?? previous?.Failed,
                    previous?.Progress, previous?.Total);
                if (status is SyncLogPolicy.StatusPartial or SyncLogPolicy.StatusFailed ||
                    (status == SyncLogPolicy.StatusSkipped && reasonCode != SyncLogPolicy.ReasonNotApplicable))
                    Interlocked.Exchange(ref _incompletePhases, 1);
                state.CompletedAt = completedAtUtc;
                state.LastWrittenAt = _timeProvider.GetUtcNow().UtcDateTime;
                await TryUpsertAsync(Entry(phase, state, completedAtUtc, status, reasonCode));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // Ook een herhaalde voltooiing mag de hoofd-sync niet beïnvloeden.
        }
    }

    private async Task TryUpsertAsync(SyncLogEntry entry)
    {
        try
        {
            await _upsert(entry);
        }
        catch
        {
            // Sync-logboek is aanvullend; een databaseprobleem mag de synchronisatie niet breken.
        }
    }

    private SyncLogEntry Entry(string phase, PhaseState state, DateTime? completedAt, string status, string? reasonCode)
    {
        var counters = state.Last;
        var entry = new SyncLogEntry(_jobId, _attempt, phase, state.StartedAt, completedAt, status,
            counters?.Fetched, counters?.Added, counters?.Updated, counters?.Failed,
            counters?.Progress, counters?.Total, reasonCode);
        SyncLogPolicy.Validate(entry);
        return entry;
    }

    private static void ValidatePhase(string phase)
    {
        if (!SyncLogPolicy.IsPhase(phase))
            throw new ArgumentException("Onbekende sync-logfase.", nameof(phase));
    }

    private static void ValidateCounts(int? fetched, int? added, int? updated, int? failed, int? progress, int? total)
    {
        foreach (var value in new[] { fetched, added, updated, failed, progress, total })
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Sync-logboekaantallen mogen niet negatief zijn.");
        }
        if (progress is { } p && total is { } t && p > t)
            throw new ArgumentException("Sync-logvoortgang kan niet groter zijn dan het totaal.");
    }

    private sealed class PhaseState(DateTime startedAt, DateTime lastWrittenAt)
    {
        public DateTime StartedAt { get; } = startedAt;
        public DateTime LastWrittenAt { get; set; } = lastWrittenAt;
        public DateTime? CompletedAt { get; set; }
        public int ProgressCalls { get; set; }
        public Counters? Last { get; set; }
    }

    private sealed record Counters(int? Fetched, int? Added, int? Updated, int? Failed, int? Progress, int? Total);
}
