using Npgsql;

namespace FunctionApp.Postgres.Sync;

/// <summary>
/// CRUD voor <c>public.syncjobs</c> (#1138) — status van een door de queue verwerkte sync-job.
/// Enige plek die deze tabel aanraakt; zowel <c>AdminSyncFunction</c> (schrijft "pending", leest
/// status) als <see cref="SyncJobProcessor"/> (schrijft "running"/"succeeded"/"failed") gebruiken
/// deze klasse in plaats van eigen SQL.
/// </summary>
internal static class SyncJobsRepository
{
    // Testnaad (#1716): elke publieke methode delegeert naar een overload met expliciete
    // connectiestring; de standaardpaden gebruiken ongewijzigd PostgresDatabaseConfig.ConnectionString.
    internal static Task CreateAsync(Guid id, string clubCode, int weekOffsetFrom, int weekOffsetTo)
        => CreateAsync(PostgresDatabaseConfig.ConnectionString, id, clubCode, weekOffsetFrom, weekOffsetTo);

    internal static Task<bool> TryClaimPendingAsync(Guid id)
        => TryClaimPendingAsync(PostgresDatabaseConfig.ConnectionString, id);

    internal static Task MarkCompletedAsync(Guid id, bool succeeded, string? errorMessage)
        => MarkCompletedAsync(PostgresDatabaseConfig.ConnectionString, id, succeeded, errorMessage);

    internal static Task<SyncJobStatus?> GetLatestOrByIdAsync(string clubCode, Guid? jobId)
        => GetLatestOrByIdAsync(PostgresDatabaseConfig.ConnectionString, clubCode, jobId);

    internal static async Task CreateAsync(string connectionString, Guid id, string clubCode, int weekOffsetFrom, int weekOffsetTo)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO public.syncjobs (id, clubcode, status, weekoffsetfrom, weekoffsetto) " +
            "VALUES (@id, @clubcode, 'pending', @weekoffsetfrom, @weekoffsetto)",
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("clubcode", clubCode);
        command.Parameters.AddWithValue("weekoffsetfrom", weekOffsetFrom);
        command.Parameters.AddWithValue("weekoffsetto", weekOffsetTo);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<bool> TryClaimPendingAsync(string connectionString, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE public.syncjobs SET status = 'running', startedat = now() " +
            "WHERE id = @id AND status = 'pending' RETURNING id",
            connection);
        command.Parameters.AddWithValue("id", id);
        return global::Planner.Shared.Sync.SyncJobTransitions.IsClaimGranted(await command.ExecuteScalarAsync());
    }

    internal static async Task MarkCompletedAsync(string connectionString, Guid id, bool succeeded, string? errorMessage)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE public.syncjobs SET status = @status, completedat = now(), errormessage = @errormessage " +
            "WHERE id = @id AND status = 'running'",
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", global::Planner.Shared.Sync.SyncJobTransitions.CompletionStatus(succeeded));
        command.Parameters.AddWithValue("errormessage", (object?)global::Planner.Shared.Sync.SyncJobTransitions.TruncateError(errorMessage) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Meest recente job voor <paramref name="clubCode"/>, of de job met <paramref name="jobId"/>
    /// als die is meegegeven. <c>null</c> als er nog geen enkele job bestaat.
    /// </summary>
    internal static async Task<SyncJobStatus?> GetLatestOrByIdAsync(string connectionString, string clubCode, Guid? jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id, status, weekoffsetfrom, weekoffsetto, createdat, startedat, completedat, errormessage " +
            "FROM public.syncjobs WHERE clubcode = @clubcode AND (@jobid::uuid IS NULL OR id = @jobid::uuid) " +
            "ORDER BY createdat DESC LIMIT 1",
            connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        command.Parameters.AddWithValue("jobid", (object?)jobId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new SyncJobStatus(
            Id: reader.GetGuid(0),
            Status: reader.GetString(1),
            WeekOffsetFrom: reader.GetInt32(2),
            WeekOffsetTo: reader.GetInt32(3),
            CreatedAt: reader.GetDateTime(4),
            StartedAt: reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            CompletedAt: reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            ErrorMessage: reader.IsDBNull(7) ? null : reader.GetString(7));
    }

}

internal sealed record SyncJobStatus(
    Guid Id,
    string Status,
    int WeekOffsetFrom,
    int WeekOffsetTo,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ErrorMessage);
