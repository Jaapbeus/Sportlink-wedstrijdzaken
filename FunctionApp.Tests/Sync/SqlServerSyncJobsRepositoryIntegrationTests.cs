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
                    () => SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id),
                    _ =>
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
                    () => SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id),
                    _ => { Interlocked.Increment(ref processCount); return Task.CompletedTask; });
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
            (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().NotBeNull();

            var processCount = 0;
            var redeliveryClaimed = await SyncJobClaimCore.ExecuteIfClaimedAsync(
                    () => SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id),
                    _ => { processCount++; return Task.CompletedTask; });

            redeliveryClaimed.Should().BeFalse();
            processCount.Should().Be(0);
            (await ReadAsync(id)).Status.Should().Be("running",
                "een redelivery mag een lopende job nooit zelf herstarten; alleen het begrensde herstel (#1839) na 30 minuten doet dat");
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
            (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, Guid.NewGuid())).Should().BeNull();
            (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().NotBeNull();
            await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: true, errorMessage: null);
            var terminalBefore = await ReadAsync(id);

            (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().BeNull();
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

    // ── #1839: begrensd herstel van onderbroken jobs ────────────────────────────────────────────

    [SqlServerFact]
    public async Task Herstel_DriePogingenDaarnaFailed_EnVerouderdeWorkerSchrijftNiets()
    {
        var id = Guid.NewGuid();
        await CreateAsync(id);
        try
        {
            (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().Be(1);
            await MaakStartOuderAsync(id, minuten: 31);
            (await HerstelAsync()).Retry.Select(r => r.JobId).Should().Contain(id);
            (await ReadAsync(id)).Status.Should().Be("pending");

            (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().Be(2);
            // De oorspronkelijke (verouderde) worker rondt alsnog af en mag poging 2 niet beïnvloeden.
            await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: true, errorMessage: null, attempt: 1);
            (await ReadAsync(id)).Status.Should().Be("running");

            await MaakStartOuderAsync(id, minuten: 31);
            (await HerstelAsync()).Retry.Select(r => r.JobId).Should().Contain(id);
            (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().Be(3);

            await MaakStartOuderAsync(id, minuten: 31);
            var laatste = await HerstelAsync();
            laatste.Retry.Select(r => r.JobId).Should().NotContain(id);
            laatste.GaveUp.Should().BeGreaterThanOrEqualTo(1);

            var state = await ReadAsync(id);
            state.Status.Should().Be("failed");
            state.ErrorMessage.Should().Be(SyncJobRecoveryPolicy.ExhaustedMessage);
            state.CompletedAt.Should().NotBeNull();
            (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().BeNull();
        }
        finally
        {
            await DeleteAsync(id);
        }
    }

    [SqlServerFact]
    public async Task Herstel_Grenswaarde_29MinutenBlijftRunning_30MinutenWordtHerplaatst()
    {
        var id = Guid.NewGuid();
        await CreateAsync(id);
        try
        {
            await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id);

            await MaakStartOuderAsync(id, minuten: 29);
            (await HerstelAsync()).Retry.Select(r => r.JobId).Should().NotContain(id);
            (await ReadAsync(id)).Status.Should().Be("running");

            await MaakStartOuderAsync(id, minuten: 30);
            (await HerstelAsync()).Retry.Select(r => r.JobId).Should().Contain(id);
            (await ReadAsync(id)).Status.Should().Be("pending");
        }
        finally
        {
            await DeleteAsync(id);
        }
    }

    [SqlServerFact]
    public async Task Herstel_ParallelAangeroepen_PlaatstEenJobHoogstensEenKeerTerug()
    {
        var id = Guid.NewGuid();
        await CreateAsync(id);
        try
        {
            await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id);
            await MaakStartOuderAsync(id, minuten: 45);

            var batches = await Task.WhenAll(HerstelAsync(), HerstelAsync(), HerstelAsync());

            batches.Count(b => b.Retry.Any(r => r.JobId == id)).Should().Be(1);
        }
        finally
        {
            await DeleteAsync(id);
        }
    }

    [SqlServerFact]
    public async Task Herstel_RaaktAfgerondeEnVerseJobsNiet()
    {
        var klaar = Guid.NewGuid();
        var vers = Guid.NewGuid();
        await CreateAsync(klaar);
        await CreateAsync(vers);
        try
        {
            await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, klaar);
            await SyncJobsRepository.MarkCompletedAsync(ConnectionString, klaar, succeeded: true, errorMessage: null, attempt: 1);
            await MaakStartOuderAsync(klaar, minuten: 120);
            await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, vers);

            var batch = await HerstelAsync();

            batch.Retry.Select(r => r.JobId).Should().NotContain([klaar, vers]);
            (await ReadAsync(klaar)).Status.Should().Be("succeeded");
            (await ReadAsync(vers)).Status.Should().Be("running");
        }
        finally
        {
            await DeleteAsync(klaar);
            await DeleteAsync(vers);
        }
    }

    private static Task<SyncJobRecoveryBatch> HerstelAsync()
        => SyncJobsRepository.RecoverInterruptedAsync(ConnectionString, SyncJobRecoveryPolicy.StaleCutoff(DateTime.UtcNow));

    private static async Task MaakStartOuderAsync(Guid id, int minuten)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "UPDATE [dbo].[SyncJobs] SET [StartedAt] = DATEADD(SECOND, -2, DATEADD(MINUTE, -@Minuten, GETUTCDATE())) WHERE [Id] = @Id",
            connection);
        command.Parameters.AddWithValue("@Minuten", minuten);
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync();
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
