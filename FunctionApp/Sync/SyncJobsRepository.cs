using Microsoft.Data.SqlClient;

namespace SportlinkFunction;

/// <summary>
/// CRUD voor <c>dbo.SyncJobs</c> (#1138) — status van een door de queue verwerkte sync-job.
/// Enige plek die deze tabel aanraakt; zowel <c>AdminSyncFunction</c> (schrijft "pending", leest
/// status) als <see cref="SyncJobProcessor"/> (schrijft "running"/"succeeded"/"failed") gebruiken
/// deze klasse in plaats van eigen SQL.
/// </summary>
internal static class SyncJobsRepository
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

    internal static async Task MarkRunningAsync(Guid id)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(
            "UPDATE [dbo].[SyncJobs] SET [Status] = 'running', [StartedAt] = GETUTCDATE() WHERE [Id] = @Id",
            connection);
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task MarkCompletedAsync(Guid id, bool succeeded, string? errorMessage)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(
            "UPDATE [dbo].[SyncJobs] SET [Status] = @Status, [CompletedAt] = GETUTCDATE(), [ErrorMessage] = @ErrorMessage WHERE [Id] = @Id",
            connection);
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Status", succeeded ? "succeeded" : "failed");
        command.Parameters.AddWithValue("@ErrorMessage", (object?)Truncate(errorMessage, 1000) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

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

        return new SyncJobStatus(
            Id: reader.GetGuid(0),
            Status: reader.GetString(1),
            WeekOffsetFrom: reader.GetInt32(2),
            WeekOffsetTo: reader.GetInt32(3),
            CreatedAt: DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
            StartedAt: reader.IsDBNull(5) ? null : DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
            CompletedAt: reader.IsDBNull(6) ? null : DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
            ErrorMessage: reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private static string? Truncate(string? value, int maxLength)
        => value is null ? null : value.Length <= maxLength ? value : value[..maxLength];
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
