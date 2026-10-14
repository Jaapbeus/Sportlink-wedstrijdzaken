using AwesomeAssertions;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Sync;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests.Sync;

/// <summary>
/// #1716 slice 3: eerste test voor de sync-jobverwerking op de productietier. <see cref="SyncJobProcessor"/>
/// legt de voortgang van een job uitsluitend vast via <see cref="SyncJobsRepository"/>
/// (pending, running, succeeded of failed) en de GUI pollt dezelfde rij. Deze tests bewaken die
/// statusovergangen, de afkapping van de foutmelding en het opzoeken per club of per job tegen een
/// echte Postgres-instantie. Testdata uitsluitend synthetisch (eigen testclubs).
/// </summary>
public class SyncJobsRepositoryIntegrationTests
{
    private const string Club = "testclub-syncjobs";
    private const string AndereClub = "testclub-syncjobs-b";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie PostgresSyncFixtureIntegrationTests.");

    [PostgresFact]
    public async Task Create_ZetJobOpPending_ZonderStart_EnZonderEinde()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();

        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, weekOffsetFrom: -1, weekOffsetTo: 3);

        var job = await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id);
        job.Should().NotBeNull();
        job!.Status.Should().Be("pending");
        job.WeekOffsetFrom.Should().Be(-1);
        job.WeekOffsetTo.Should().Be(3);
        job.StartedAt.Should().BeNull();
        job.CompletedAt.Should().BeNull();
        job.ErrorMessage.Should().BeNull();
    }

    [PostgresFact]
    public async Task MarkRunning_ZetStatusRunning_EnStartTijd()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, 0, 1);

        await SyncJobsRepository.MarkRunningAsync(ConnectionString, id);

        var job = await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id);
        job!.Status.Should().Be("running");
        job.StartedAt.Should().NotBeNull();
        job.CompletedAt.Should().BeNull();
    }

    [PostgresFact]
    public async Task MarkCompleted_Geslaagd_ZetSucceeded_ZonderFoutmelding()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, 0, 1);
        await SyncJobsRepository.MarkRunningAsync(ConnectionString, id);

        await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: true, errorMessage: null);

        var job = await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id);
        job!.Status.Should().Be("succeeded");
        job.CompletedAt.Should().NotBeNull();
        job.ErrorMessage.Should().BeNull();
    }

    [PostgresFact]
    public async Task MarkCompleted_Mislukt_ZetFailed_MetFoutmelding()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, 0, 1);

        await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: false, errorMessage: "testfout");

        var job = await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id);
        job!.Status.Should().Be("failed");
        job.CompletedAt.Should().NotBeNull();
        job.ErrorMessage.Should().Be("testfout");
    }

    [PostgresFact]
    public async Task MarkCompleted_LangeFoutmelding_WordtOpDuizendTekensAfgekapt()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, 0, 1);

        await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: false, errorMessage: new string('x', 1500));

        var job = await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id);
        job!.ErrorMessage.Should().Be(new string('x', 1000),
            "de kolom is varchar(1000); een langere melding mag de statusupdate niet laten falen");
    }

    [PostgresFact]
    public async Task GetLatest_ZonderJobId_GeeftNieuwsteJobVanEigenClub()
    {
        await SchoonAsync();
        var oud = Guid.NewGuid();
        var nieuw = Guid.NewGuid();
        var vanAndereClub = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, oud, Club, 0, 1);
        await Task.Delay(20);
        await SyncJobsRepository.CreateAsync(ConnectionString, nieuw, Club, 0, 2);
        await Task.Delay(20);
        await SyncJobsRepository.CreateAsync(ConnectionString, vanAndereClub, AndereClub, 0, 9);

        var job = await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, jobId: null);

        job!.Id.Should().Be(nieuw);
        job.WeekOffsetTo.Should().Be(2);
    }

    [PostgresFact]
    public async Task GetLatest_MetJobId_GeeftAlleenDieJob_EnNooitEenJobVanEenAndereClub()
    {
        await SchoonAsync();
        var oud = Guid.NewGuid();
        var nieuw = Guid.NewGuid();
        var vanAndereClub = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, oud, Club, 0, 1);
        await Task.Delay(20);
        await SyncJobsRepository.CreateAsync(ConnectionString, nieuw, Club, 0, 2);
        await SyncJobsRepository.CreateAsync(ConnectionString, vanAndereClub, AndereClub, 0, 9);

        (await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, oud))!.Id.Should().Be(oud);
        (await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, vanAndereClub))
            .Should().BeNull("een job van een andere club is niet opvraagbaar via deze club (ClubCode-isolatie)");
    }

    [PostgresFact]
    public async Task GetLatest_ZonderJobs_GeeftNull()
    {
        await SchoonAsync();

        (await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, jobId: null)).Should().BeNull();
    }

    private static async Task SchoonAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM public.syncjobs WHERE clubcode IN (@a, @b)", conn);
        cmd.Parameters.AddWithValue("a", Club);
        cmd.Parameters.AddWithValue("b", AndereClub);
        await cmd.ExecuteNonQueryAsync();
    }
}
