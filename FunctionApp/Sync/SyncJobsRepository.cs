using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Planner.Shared.Sync;

namespace SportlinkFunction;

/// <summary>
/// CRUD voor <c>dbo.SyncJobs</c> (#1138) — status van een door de queue verwerkte sync-job.
/// Enige plek die deze tabel aanraakt; zowel <c>AdminSyncFunction</c> (schrijft "pending", leest
/// status) als <see cref="SyncJobProcessor"/> (schrijft "running"/"succeeded"/"failed") gebruiken
/// deze klasse in plaats van eigen SQL.
/// </summary>
internal static partial class SyncJobsRepository
{
    internal static async Task CreateAsync(Guid id, string clubCode, int weekOffsetFrom, int weekOffsetTo)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(
            "INSERT INTO [dbo].[SyncJobs] ([Id], [ClubCode], [Status], [WeekOffsetFrom], [WeekOffsetTo]) " +
            "VALUES (@Id, @ClubCode, 'pending', @WeekOffsetFrom, @WeekOffsetTo)",
            connection);
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        command.Parameters.AddWithValue("@WeekOffsetFrom", weekOffsetFrom);
        command.Parameters.AddWithValue("@WeekOffsetTo", weekOffsetTo);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Atomaire pending-to-running-claim (#1839): geeft het pogingnummer van deze claim terug,
    /// <c>null</c> als de job niet meer pending is. Het nummer is de duurzame pogingseigendom:
    /// afrondingen voor een oudere poging worden door <see cref="MarkCompletedAsync(Guid, bool, string?, int)"/> genegeerd.
    /// </summary>
    internal static async Task<int?> TryClaimAttemptAsync(string connectionString, Guid id)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "UPDATE [dbo].[SyncJobs] SET [Status] = 'running', [StartedAt] = GETUTCDATE(), [Attempt] = [Attempt] + 1 " +
            "OUTPUT INSERTED.[Attempt] WHERE [Id] = @Id AND [Status] = 'pending' " +
            "AND ([Attempt] > 0 OR [CreatedAt] > @Cutoff)",
            connection);
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.Add("@Cutoff", System.Data.SqlDbType.DateTime2).Value = SyncJobRecoveryPolicy.StaleCutoff(DateTime.UtcNow);
        return SyncJobTransitions.ReadClaimedAttempt(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Rondt de job af. Met <paramref name="attempt"/> alleen voor precies die poging: een verouderde
    /// poging (de job is intussen herplaatst en opnieuw geclaimd) schrijft niets. Zonder pogingnummer
    /// (alleen tests) geldt alleen de running-voorwaarde.
    /// </summary>
    internal static Task MarkCompletedAsync(Guid id, bool succeeded, string? errorMessage, int attempt)
        => MarkCompletedAsync(SystemUtilities.DatabaseConfig.ConnectionString, id, succeeded, errorMessage, attempt);

    internal static Task MarkOutcomeAsync(Guid id, SyncUitkomst outcome, int attempt, bool incompletePhases = false)
        => MarkOutcomeAsync(SystemUtilities.DatabaseConfig.ConnectionString, id, outcome, attempt, incompletePhases);

    internal static Task MarkOutcomeAsync(string connectionString, Guid id, SyncUitkomst outcome, int attempt, bool incompletePhases = false)
        => MarkTerminalAsync(connectionString, id, SyncJobTransitions.CompletionStatus(outcome, incompletePhases),
            outcome.GedeeltelijkMislukt ? outcome.JobFoutmelding() : null, attempt);

    internal static Task MarkCompletedAsync(string connectionString, Guid id, bool succeeded, string? errorMessage, int? attempt = null)
        => MarkTerminalAsync(connectionString, id, SyncJobTransitions.CompletionStatus(succeeded), errorMessage, attempt);

    private static async Task MarkTerminalAsync(string connectionString, Guid id, string status, string? errorMessage, int? attempt)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "UPDATE [dbo].[SyncJobs] SET [Status] = @Status, [CompletedAt] = GETUTCDATE(), [ErrorMessage] = @ErrorMessage " +
            "WHERE [Id] = @Id AND [Status] = 'running' AND (@Attempt IS NULL OR [Attempt] = @Attempt)",
            connection);
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Attempt", (object?)attempt ?? DBNull.Value);
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@ErrorMessage", (object?)SyncJobTransitions.TruncateError(errorMessage) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Eén atomaire herstelstap (#1839): uitgeputte onderbroken jobs gaan naar "failed", de overige
    /// onderbroken jobs (en "pending"-jobs waarvan het bericht kwijt is) gaan terug naar "pending"
    /// met een nieuw <c>StartedAt</c>. Drempels komen uit <see cref="SyncJobRecoveryPolicy"/>.
    /// </summary>
    internal static async Task<SyncJobRecoveryBatch> RecoverInterruptedAsync(string connectionString, DateTime cutoffUtc)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        // Eén UPDATE: uitgeputte pogingen en verlopen eerste berichten eindigen atomair; alleen pending wordt herplaatst.
        await using var command = new SqlCommand(
            "UPDATE [dbo].[SyncJobs] SET " +
            "[Status] = CASE WHEN (([Status] = 'running' AND [Attempt] >= @Max) OR ([Status] = 'pending' AND [Attempt] = 0)) THEN 'failed' ELSE 'pending' END, " +
            "[CompletedAt] = CASE WHEN (([Status] = 'running' AND [Attempt] >= @Max) OR ([Status] = 'pending' AND [Attempt] = 0)) THEN GETUTCDATE() ELSE [CompletedAt] END, " +
            "[ErrorMessage] = CASE WHEN (([Status] = 'running' AND [Attempt] >= @Max) OR ([Status] = 'pending' AND [Attempt] = 0)) THEN CASE WHEN [Attempt] = 0 THEN @NeverStarted ELSE @Message END ELSE [ErrorMessage] END, " +
            "[StartedAt] = CASE WHEN (([Status] = 'running' AND [Attempt] >= @Max) OR ([Status] = 'pending' AND [Attempt] = 0)) THEN [StartedAt] ELSE GETUTCDATE() END " +
            "OUTPUT INSERTED.[Id], INSERTED.[ClubCode], INSERTED.[WeekOffsetFrom], INSERTED.[WeekOffsetTo], INSERTED.[Status] " +
            "WHERE (([Status] = 'running' OR ([Status] = 'pending' AND [Attempt] > 0)) AND [StartedAt] <= @Cutoff) " +
            "OR ([Status] = 'pending' AND [Attempt] = 0 AND [CreatedAt] <= @Cutoff)",
            connection);
        command.Parameters.AddWithValue("@Max", SyncJobRecoveryPolicy.MaxAttempts);
        command.Parameters.AddWithValue("@Message", SyncJobRecoveryPolicy.ExhaustedMessage);
        command.Parameters.AddWithValue("@NeverStarted", SyncJobRecoveryPolicy.NeverStartedMessage);
        command.Parameters.AddWithValue("@Cutoff", cutoffUtc);
        await using var reader = await command.ExecuteReaderAsync();
        return await SyncJobRecoveryCore.ReadBatchAsync(reader);
    }

    /// <summary>
    /// Herstelt onderbroken jobs als onderdeel van bestaande activiteit (#1839); breekt die nooit.
    /// </summary>
    internal static Task HerstelOnderbrokenAsync(ILogger log)
        => SyncJobRecoveryCore.TryRunAsync(
            cutoff => RecoverInterruptedAsync(SystemUtilities.DatabaseConfig.ConnectionString, cutoff), SyncJobsQueue.CreateClient, log);

    /// <summary>
    /// Meest recente job voor <paramref name="clubCode"/>, of de job met <paramref name="jobId"/>
    /// als die is meegegeven. <c>null</c> als er nog geen enkele job bestaat.
    /// </summary>
    internal static async Task<SyncJobStatus?> GetLatestOrByIdAsync(string clubCode, Guid? jobId)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(
            "SELECT TOP 1 [Id], [Status], [WeekOffsetFrom], [WeekOffsetTo], [CreatedAt], [StartedAt], [CompletedAt], [ErrorMessage] " +
            "FROM [dbo].[SyncJobs] WHERE [ClubCode] = @ClubCode AND (@JobId IS NULL OR [Id] = @JobId) " +
            "ORDER BY [CreatedAt] DESC",
            connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        command.Parameters.AddWithValue("@JobId", (object?)jobId ?? DBNull.Value);
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return SyncJobStatus.FromReader(reader);
    }

}
