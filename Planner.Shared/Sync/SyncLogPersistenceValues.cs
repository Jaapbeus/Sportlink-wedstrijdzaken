using System.Data.Common;

namespace Planner.Shared.Sync;

/// <summary>Gedeelde, database-onafhankelijke vorm van logboekrijen en schrijfwaarden.</summary>
public static class SyncLogPersistenceValues
{
    public static void ValidateUpsert(string clubCode, SyncLogEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clubCode);
        SyncLogPolicy.Validate(entry);
    }

    public static void ValidateQuery(string clubCode, IReadOnlyCollection<Guid> jobIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clubCode);
        ArgumentNullException.ThrowIfNull(jobIds);
        if (jobIds.Count > 100)
            throw new ArgumentOutOfRangeException(nameof(jobIds), "Maximaal 100 sync-jobs per logboekvraag.");
    }

    public static Task<IReadOnlyList<SyncLogEntry>> QueryJobsAsync(string clubCode,
        IReadOnlyCollection<Guid> jobIds, Func<Guid[], Task<IReadOnlyList<SyncLogEntry>>> query)
    {
        ValidateQuery(clubCode, jobIds);
        return jobIds.Count == 0
            ? Task.FromResult<IReadOnlyList<SyncLogEntry>>(Array.Empty<SyncLogEntry>())
            : query(jobIds.Distinct().ToArray());
    }

    public static DateTime RetentionCutoff(string clubCode, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clubCode);
        return RetentionCutoff(utcNow);
    }

    public static DateTime RetentionCutoff(DateTime utcNow)
    {
        SyncLogPolicy.RequireUtc(utcNow, nameof(utcNow));
        return utcNow.AddDays(-SyncLogPolicy.RetentionDays);
    }

    public static bool IsCurrentAttempt(object? currentAttempt, int attempt)
    {
        if (currentAttempt is null)
            throw new InvalidOperationException("Sync-job niet gevonden.");
        return Convert.ToInt32(currentAttempt) == attempt;
    }

    public static async Task<IReadOnlyList<SyncLogEntry>> ReadAllAsync(DbDataReader reader, bool specifyUtc)
    {
        var results = new List<SyncLogEntry>();
        while (await reader.ReadAsync())
            results.Add(Read(reader, specifyUtc));
        return results;
    }

    public static SyncLogEntry Read(DbDataReader reader, bool specifyUtc)
    {
        DateTime Utc(int ordinal) => specifyUtc
            ? DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc)
            : reader.GetDateTime(ordinal);
        int? Count(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

        return new SyncLogEntry(
            reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), Utc(3),
            reader.IsDBNull(4) ? null : Utc(4), reader.GetString(5),
            Count(6), Count(7), Count(8), Count(9), Count(10), Count(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    public static IEnumerable<(string Name, object Value)> Parameters(string clubCode, SyncLogEntry entry)
    {
        yield return ("jobid", entry.JobId);
        yield return ("clubcode", clubCode);
        yield return ("attempt", entry.Attempt);
        yield return ("phase", entry.Phase);
        yield return ("startedat", entry.StartedAt);
        yield return ("completedat", (object?)entry.CompletedAt ?? DBNull.Value);
        yield return ("status", entry.Status);
        yield return ("fetched", (object?)entry.Fetched ?? DBNull.Value);
        yield return ("added", (object?)entry.Added ?? DBNull.Value);
        yield return ("updated", (object?)entry.Updated ?? DBNull.Value);
        yield return ("failed", (object?)entry.Failed ?? DBNull.Value);
        yield return ("progress", (object?)entry.Progress ?? DBNull.Value);
        yield return ("total", (object?)entry.Total ?? DBNull.Value);
        yield return ("reasoncode", (object?)entry.ReasonCode ?? DBNull.Value);
    }
}
