using Npgsql;
using FunctionApp.Postgres.Sync;
using Database.Postgres.Tests;
using AwesomeAssertions;
using Xunit;

namespace FunctionApp.Postgres.Tests.Sync;

public class SyncJobStartIntegrationTests
{
    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull ?? throw new InvalidOperationException("Testdatabase ontbreekt.");

    [PostgresFact]
    public async Task ConcurrentStarts_CreateExactlyOneJob_AndReuseIt()
    {
        const string club = "ALLSTARS-START";
        await DeleteAsync(club);
        try
        {
            var choices = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
                SyncJobsRepository.GetOrCreateActiveAsync(ConnectionString, Guid.NewGuid(), club, -1, 4)));
            choices.Count(c => c.Created).Should().Be(1);
            choices.Select(c => c.Job.Id).Distinct().Should().ContainSingle();
            var active = await SyncJobsRepository.GetActiveAsync(ConnectionString, club);
            active!.Id.Should().Be(choices[0].Job.Id);
            (await SyncJobsRepository.GetActiveAsync(ConnectionString, "ALLSTARS-OTHER")).Should().BeNull();
            var foreign = await SyncJobsRepository.GetRecentAsync(ConnectionString, "ALLSTARS-OTHER", active.Id);
            foreign.Should().BeEmpty();
        }
        finally { await DeleteAsync(club); }
    }

    [PostgresFact]
    public async Task FailedEnqueue_AllowsNewStart_WithoutChangingClaimedJob()
    {
        const string club = "ALLSTARS-QUEUE";
        await DeleteAsync(club);
        try
        {
            var first = await SyncJobsRepository.GetOrCreateActiveAsync(ConnectionString, Guid.NewGuid(), club, 0, 1);
            await SyncJobsRepository.MarkEnqueueFailedAsync(ConnectionString, first.Job.Id, club);
            var second = await SyncJobsRepository.GetOrCreateActiveAsync(ConnectionString, Guid.NewGuid(), club, 0, 1);
            second.Created.Should().BeTrue();
            second.Job.Id.Should().NotBe(first.Job.Id);
            await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, second.Job.Id);
            await SyncJobsRepository.MarkEnqueueFailedAsync(ConnectionString, second.Job.Id, club);
            (await SyncJobsRepository.GetActiveAsync(ConnectionString, club))!.Status.Should().Be("running");
            var history = await SyncJobsRepository.GetRecentAsync(ConnectionString, club, first.Job.Id);
            history.Single(j => j.Id == first.Job.Id).Status.Should().Be("failed");
        }
        finally { await DeleteAsync(club); }
    }

    [PostgresFact]
    public async Task CompletedPipeline_PreservesPartialOutcome_AndAttemptOwnership()
    {
        const string club = "ALLSTARS-OUTCOME";
        await DeleteAsync(club);
        try
        {
            var job = await SyncJobsRepository.GetOrCreateActiveAsync(ConnectionString, Guid.NewGuid(), club, 0, 1);
            var attempt = await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, job.Job.Id);
            var outcome = new global::Planner.Shared.Sync.SyncUitkomst();
            outcome.Registreer(global::Planner.Shared.Sync.SyncUitkomst.FaseTeams, true);
            await SyncJobsRepository.MarkOutcomeAsync(ConnectionString, job.Job.Id, outcome, attempt!.Value + 1);
            (await SyncJobsRepository.GetActiveAsync(ConnectionString, club))!.Status.Should().Be("running");
            await SyncJobsRepository.MarkOutcomeAsync(ConnectionString, job.Job.Id, outcome, attempt.Value);
            var history = await SyncJobsRepository.GetRecentAsync(ConnectionString, club, job.Job.Id);
            history.Single(j => j.Id == job.Job.Id).Status.Should().Be("partial");
            await SyncJobsRepository.MarkCompletedAsync(ConnectionString, job.Job.Id, false, "late failure", attempt);
            (await SyncJobsRepository.GetRecentAsync(ConnectionString, club, job.Job.Id)).Single().Status.Should().Be("partial");
        }
        finally { await DeleteAsync(club); }
    }

    private static async Task DeleteAsync(string club)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DELETE FROM public.syncjobs WHERE ClubCode=@club", connection);
        command.Parameters.AddWithValue("@club", club);
        await command.ExecuteNonQueryAsync();
    }
}
