using System.Data.Common;

namespace Planner.Shared.Sync;

/// <summary>
/// Status van een sync-job zoals de GUI die pollt (#1138). Gedeeld door beide databasetiers: de
/// repository's vullen hem uit hun eigen tabel, de vorm is tier-onafhankelijk.
/// </summary>
public sealed record SyncJobStatus(
    Guid Id,
    string Status,
    int WeekOffsetFrom,
    int WeekOffsetTo,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ErrorMessage)
{
    public int Attempt { get; init; }

    /// <summary>
    /// Leest de huidige rij van <paramref name="reader"/> met de kolomvolgorde id, status,
    /// weekoffsetfrom, weekoffsetto, createdat, startedat, completedat, errormessage. De tijden
    /// zijn UTC (database UTC, Blazor lokale tijd); SQL Server levert ze zonder <c>Kind</c>.
    /// </summary>
    public static SyncJobStatus FromReader(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new SyncJobStatus(
            Id: reader.GetGuid(0),
            Status: reader.GetString(1),
            WeekOffsetFrom: reader.GetInt32(2),
            WeekOffsetTo: reader.GetInt32(3),
            CreatedAt: DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
            StartedAt: reader.IsDBNull(5) ? null : DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
            CompletedAt: reader.IsDBNull(6) ? null : DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
            ErrorMessage: reader.IsDBNull(7) ? null : reader.GetString(7))
        { Attempt = reader.FieldCount > 8 ? reader.GetInt32(8) : 0 };
    }
}
