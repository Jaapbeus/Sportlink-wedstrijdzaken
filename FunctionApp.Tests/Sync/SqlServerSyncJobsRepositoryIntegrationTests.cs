using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Planner.Shared.Sync;
using SportlinkFunction;
using Xunit;

namespace FunctionApp.Tests.Sync;

public sealed class SqlServerSyncJobsRepositoryIntegrationTests
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)
        ?? throw new InvalidOperationException($"{SqlServerFactAttribute.EnvVar} niet gezet.");

    [SqlServerFact]
    public async Task TryClaimPending_ConcurrenteWorkers_AlleenEenStartSync_EnDuplicaatWijzigtGeenVelden()
    {
        var id = Guid.NewGuid();
        await CreateAsync(id);
        try
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var processCount = 0;

            async Task<bool> WorkerAsync()
            {
                await start.Task;
                return await SyncJobClaimCore.ExecuteIfClaimedAsync(
                    id,
                    claimId => SyncJobsRepository.TryClaimPendingAsync(ConnectionString, claimId),
                    () =>
                    {
                        Interlocked.Increment(ref processCount);
                        return Task.CompletedTask;
                    });
            }

            var first = WorkerAsync();
            var second = WorkerAsync();
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            results.Count(claimed => claimed).Should().Be(1);
            processCount.Should().Be(1);

            var claimedState = await ReadAsync(id);
            claimedState.Status.Should().Be("running");
            claimedState.StartedAt.Should().NotBeNull();
            claimedState.CompletedAt.Should().BeNull();

            var duplicateClaimed = await SyncJobClaimCore.ExecuteIfClaimedAsync(
                id,
                claimId => SyncJobsRepository.TryClaimPendingAsync(ConnectionString, claimId),
                () => { Interlocked.Increment(ref processCount); return Task.CompletedTask; });
            duplicateClaimed.Should().BeFalse();
            processCount.Should().Be(1);

            var duplicateState = await ReadAsync(id);
            duplicateState.Should().Be(claimedState);
        }
        finally
        {
            await DeleteAsync(id);
        }
    }

    [SqlServerFact]
    public async Task TryClaimPending_AfgebrokenRunBlijftRunningEnWordtBijRedeliveryNietDubbelUitgevoerd()
    {
        var id = Guid.NewGuid();
        await CreateAsync(id);
        try
        {
            (await SyncJobsRepository.TryClaimPendingAsync(ConnectionString, id)).Should().BeTrue();

            var processCount = 0;
            var redeliveryClaimed = await SyncJobClaimCore.ExecuteIfClaimedAsync(
                id,
                claimId => SyncJobsRepository.TryClaimPendingAsync(ConnectionString, claimId),
                () => { processCount++; return Task.CompletedTask; });

            redeliveryClaimed.Should().BeFalse();
            processCount.Should().Be(0);
            (await ReadAsync(id)).Status.Should().Be("running",
                "the existing schema cannot tell whether the original worker crashed or is still running; recovery policy remains unresolved");
        }
        finally
        {
            await DeleteAsync(id);
        }
    }

    [SqlServerFact]
    public async Task TryClaimPending_OntbrekendeOfTerminaleJob_EnLateCompletionWijzigenGeenVelden()
    {
        var id = Guid.NewGuid();
        await CreateAsync(id);
        try
        {
            (await SyncJobsRepository.TryClaimPendingAsync(ConnectionString, Guid.NewGuid())).Should().BeFalse();
            (await SyncJobsRepository.TryClaimPendingAsync(ConnectionString, id)).Should().BeTrue();
            await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: true, errorMessage: null);
            var terminalBefore = await ReadAsync(id);

            (await SyncJobsRepository.TryClaimPendingAsync(ConnectionString, id)).Should().BeFalse();
            await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: false, errorMessage: "late duplicate");
            var terminalAfter = await ReadAsync(id);

            terminalAfter.Should().Be(terminalBefore);
            terminalAfter.Status.Should().Be("succeeded");
            terminalAfter.ErrorMessage.Should().BeNull();
        }
        finally
        {
            await DeleteAsync(id);
        }
    }

    private static async Task CreateAsync(Guid id)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "INSERT INTO [dbo].[SyncJobs] ([Id], [ClubCode], [WeekOffsetFrom], [WeekOffsetTo]) " +
            "VALUES (@Id, @ClubCode, 0, 1)",
            connection);
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@ClubCode", "t" + id.ToString("N")[..12]);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<JobState> ReadAsync(Guid id)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [Status], [StartedAt], [CompletedAt], [ErrorMessage] FROM [dbo].[SyncJobs] WHERE [Id] = @Id",
            connection);
        command.Parameters.AddWithValue("@Id", id);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new JobState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task DeleteAsync(Guid id)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("DELETE FROM [dbo].[SyncJobs] WHERE [Id] = @Id", connection);
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record JobState(string Status, DateTime? StartedAt, DateTime? CompletedAt, string? ErrorMessage);
}
