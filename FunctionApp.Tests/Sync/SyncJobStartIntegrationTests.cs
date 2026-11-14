using Microsoft.Data.SqlClient;
using SportlinkFunction;
using FunctionApp.Tests.Feedback;
using AwesomeAssertions;
using Xunit;

namespace FunctionApp.Tests.Sync;

public class SqlServerSyncJobsRepositoryIntegrationTests_Start
{
    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar) ?? throw new InvalidOperationException("Testdatabase ontbreekt.");

    [SqlServerFact]
    public async Task ConcurrentStarts_CreateExactlyOneJob_AndReuseIt()
    {
        const string club = "ALLSTARS-START";
        await DeleteAsync(club);
        try
        {
            var choices = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
                SyncJobsRepository.GetOrCreateActiveAsync(ConnectionString, Guid.NewGuid(), i % 2 == 0 ? club : club.ToLowerInvariant(), -1, 4)));
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

    [SqlServerFact]
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

    private static async Task DeleteAsync(string club)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("DELETE FROM dbo.SyncJobs WHERE ClubCode=@club", connection);
        command.Parameters.AddWithValue("@club", club);
        await command.ExecuteNonQueryAsync();
    }
}
