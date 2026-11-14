using Npgsql;
using Microsoft.Extensions.Logging;
using Planner.Shared.Sync;

namespace FunctionApp.Postgres.Sync;

/// <summary>
/// CRUD voor <c>public.syncjobs</c> (#1138) — status van een door de queue verwerkte sync-job.
/// Enige plek die deze tabel aanraakt; zowel <c>AdminSyncFunction</c> (schrijft "pending", leest
/// status) als <see cref="SyncJobProcessor"/> (schrijft "running"/"succeeded"/"failed") gebruiken
/// deze klasse in plaats van eigen SQL.
/// </summary>
internal static partial class SyncJobsRepository
{
    // Testnaad (#1716): elke publieke methode delegeert naar een overload met expliciete
    // connectiestring; de standaardpaden gebruiken ongewijzigd PostgresDatabaseConfig.ConnectionString.
    internal static Task CreateAsync(Guid id, string clubCode, int weekOffsetFrom, int weekOffsetTo)
        => CreateAsync(PostgresDatabaseConfig.ConnectionString, id, clubCode, weekOffsetFrom, weekOffsetTo);

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

    /// <summary>
    /// Atomaire pending-to-running-claim (#1839): geeft het pogingnummer van deze claim terug,
    /// <c>null</c> als de job niet meer pending is. Het nummer is de duurzame pogingseigendom:
    /// afrondingen voor een oudere poging worden door <see cref="MarkCompletedAsync(Guid, bool, string?, int)"/> genegeerd.
    /// </summary>
    internal static async Task<int?> TryClaimAttemptAsync(string connectionString, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE public.syncjobs SET status = 'running', startedat = now(), attempt = attempt + 1 " +
            "WHERE id = @id AND status = 'pending' RETURNING attempt",
            connection);
        command.Parameters.AddWithValue("id", id);
        return SyncJobTransitions.ReadClaimedAttempt(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Rondt de job af. Met <paramref name="attempt"/> alleen voor precies die poging: een verouderde
    /// poging (de job is intussen herplaatst en opnieuw geclaimd) schrijft niets. Zonder pogingnummer
    /// (alleen tests) geldt alleen de running-voorwaarde.
    /// </summary>
    internal static Task MarkCompletedAsync(Guid id, bool succeeded, string? errorMessage, int attempt)
        => MarkCompletedAsync(PostgresDatabaseConfig.ConnectionString, id, succeeded, errorMessage, attempt);

    internal static async Task MarkCompletedAsync(string connectionString, Guid id, bool succeeded, string? errorMessage, int? attempt = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE public.syncjobs SET status = @status, completedat = now(), errormessage = @errormessage " +
            "WHERE id = @id AND status = 'running' AND (@attempt::int IS NULL OR attempt = @attempt::int)",
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", (object?)attempt ?? DBNull.Value);
        command.Parameters.AddWithValue("status", SyncJobTransitions.CompletionStatus(succeeded));
        command.Parameters.AddWithValue("errormessage", (object?)SyncJobTransitions.TruncateError(errorMessage) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Eén atomaire herstelstap (#1839): uitgeputte onderbroken jobs gaan naar "failed", de overige
    /// onderbroken jobs (en "pending"-jobs waarvan het bericht kwijt is) gaan terug naar "pending"
    /// met een nieuw <c>startedat</c>. Drempels komen uit <see cref="SyncJobRecoveryPolicy"/>.
    /// </summary>
    internal static async Task<SyncJobRecoveryBatch> RecoverInterruptedAsync(string connectionString, DateTime cutoffUtc)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        // Een UPDATE, dus atomair: uitgeput (running, poging >= max) wordt failed, de rest pending.
        await using var command = new NpgsqlCommand(
            "UPDATE public.syncjobs SET " +
            "status = CASE WHEN status = 'running' AND attempt >= @max THEN 'failed' ELSE 'pending' END, " +
            "completedat = CASE WHEN status = 'running' AND attempt >= @max THEN now() ELSE completedat END, " +
            "errormessage = CASE WHEN status = 'running' AND attempt >= @max THEN @message ELSE errormessage END, " +
            "startedat = CASE WHEN status = 'running' AND attempt >= @max THEN startedat ELSE now() END " +
            "WHERE status IN ('running', 'pending') AND (status = 'running' OR attempt > 0) AND startedat <= @cutoff " +
            "RETURNING id, clubcode, weekoffsetfrom, weekoffsetto, status",
            connection);
        command.Parameters.AddWithValue("max", SyncJobRecoveryPolicy.MaxAttempts);
        command.Parameters.AddWithValue("message", SyncJobRecoveryPolicy.ExhaustedMessage);
        command.Parameters.AddWithValue("cutoff", DateTime.SpecifyKind(cutoffUtc, DateTimeKind.Utc));
        await using var reader = await command.ExecuteReaderAsync();
        return await SyncJobRecoveryCore.ReadBatchAsync(reader);
    }

    /// <summary>
    /// Herstelt onderbroken jobs als onderdeel van bestaande activiteit (#1839); breekt die nooit.
    /// </summary>
    internal static Task HerstelOnderbrokenAsync(ILogger log)
        => SyncJobRecoveryCore.TryRunAsync(
            cutoff => RecoverInterruptedAsync(PostgresDatabaseConfig.ConnectionString, cutoff), SyncJobsQueue.CreateClient, log);

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

        return SyncJobStatus.FromReader(reader);
    }

}
