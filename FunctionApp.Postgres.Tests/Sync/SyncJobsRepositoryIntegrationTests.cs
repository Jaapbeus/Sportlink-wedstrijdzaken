using AwesomeAssertions;
using Database.Postgres.Tests;
using Planner.Shared.Sync;
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
    public async Task TryClaimPending_ConcurrenteWorkers_AlleenEenStartSync_EnDuplicaatWijzigtGeenVelden()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, 0, 1);

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
        var job = await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id);
        job!.Status.Should().Be("running");
        job.StartedAt.Should().NotBeNull();
        job.CompletedAt.Should().BeNull();

        var startedAt = job.StartedAt;
        var claimedAgain = await SyncJobClaimCore.ExecuteIfClaimedAsync(
                    () => SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id),
                    _ => { Interlocked.Increment(ref processCount); return Task.CompletedTask; });
        claimedAgain.Should().BeFalse();
        processCount.Should().Be(1);
        var duplicate = await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id);
        duplicate!.Status.Should().Be("running");
        duplicate.StartedAt.Should().Be(startedAt);
        duplicate.CompletedAt.Should().BeNull();
        duplicate.ErrorMessage.Should().BeNull();
    }

    [PostgresFact]
    public async Task TryClaimPending_AfgebrokenRunBlijftRunningEnWordtBijRedeliveryNietDubbelUitgevoerd()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, 0, 1);

        (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().NotBeNull();

        var processCount = 0;
        var redeliveryClaimed = await SyncJobClaimCore.ExecuteIfClaimedAsync(
                    () => SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id),
                    _ => { processCount++; return Task.CompletedTask; });

        redeliveryClaimed.Should().BeFalse();
        processCount.Should().Be(0);
        (await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id))!.Status.Should().Be("running",
            "the existing schema cannot tell whether the original worker crashed or is still running; recovery policy remains unresolved");
    }

    [PostgresFact]
    public async Task MarkCompleted_Geslaagd_ZetSucceeded_ZonderFoutmelding()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, 0, 1);
        await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id);

        await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: true, errorMessage: null);
        await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: false, errorMessage: "late duplicate");

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
        await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id);

        await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: false, errorMessage: "testfout");

        var job = await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id);
        job!.Status.Should().Be("failed");
        job.CompletedAt.Should().NotBeNull();
        job.ErrorMessage.Should().Be("testfout");
    }

    [PostgresFact]
    public async Task TryClaimPending_OntbrekendeOfTerminaleJob_WordtNietAangepast()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, 0, 1);
        await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id);
        await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: true, errorMessage: null);

        var terminalBefore = await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id);
        (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, Guid.NewGuid())).Should().BeNull();
        (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().BeNull();
        await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: false, errorMessage: "late duplicate");
        var terminalAfter = await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id);

        terminalAfter!.Status.Should().Be("succeeded");
        terminalAfter.StartedAt.Should().Be(terminalBefore!.StartedAt);
        terminalAfter.CompletedAt.Should().Be(terminalBefore.CompletedAt);
        terminalAfter.ErrorMessage.Should().BeNull();
    }

    [PostgresFact]
    public async Task MarkCompleted_LangeFoutmelding_WordtOpDuizendTekensAfgekapt()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, 0, 1);
        await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id);

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

    // ── #1839: begrensd herstel van onderbroken jobs ────────────────────────────────────────────

    [PostgresFact]
    public async Task Herstel_DriePogingenDaarnaFailed_EnVerouderdeWorkerSchrijftNiets()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, 0, 1);

        (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().Be(1);
        await MaakStartOuderAsync(id, minuten: 31);
        var eerste = await HerstelAsync();
        eerste.Retry.Select(r => r.JobId).Should().Contain(id);
        (await StatusAsync(id)).Should().Be("pending");

        (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().Be(2);
        // De oorspronkelijke (verouderde) worker rondt alsnog af en mag poging 2 niet beïnvloeden.
        await SyncJobsRepository.MarkCompletedAsync(ConnectionString, id, succeeded: true, errorMessage: null, attempt: 1);
        (await StatusAsync(id)).Should().Be("running");

        await MaakStartOuderAsync(id, minuten: 31);
        (await HerstelAsync()).Retry.Select(r => r.JobId).Should().Contain(id);
        (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().Be(3);

        await MaakStartOuderAsync(id, minuten: 31);
        var laatste = await HerstelAsync();
        laatste.Retry.Select(r => r.JobId).Should().NotContain(id);
        laatste.GaveUp.Should().BeGreaterThanOrEqualTo(1);

        var job = await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id);
        job!.Status.Should().Be("failed");
        job.ErrorMessage.Should().Be(SyncJobRecoveryPolicy.ExhaustedMessage);
        job.CompletedAt.Should().NotBeNull();

        (await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id)).Should().BeNull("een opgegeven job wordt nooit meer geclaimd");
    }

    [PostgresFact]
    public async Task Herstel_Grenswaarde_29MinutenBlijftRunning_30MinutenWordtHerplaatst()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, 0, 1);
        await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id);

        await MaakStartOuderAsync(id, minuten: 29);
        (await HerstelAsync()).Retry.Select(r => r.JobId).Should().NotContain(id);
        (await StatusAsync(id)).Should().Be("running");

        await MaakStartOuderAsync(id, minuten: 30);
        (await HerstelAsync()).Retry.Select(r => r.JobId).Should().Contain(id);
        (await StatusAsync(id)).Should().Be("pending");
    }

    [PostgresFact]
    public async Task Herstel_ParallelAangeroepen_PlaatstEenJobHoogstensEenKeerTerug()
    {
        await SchoonAsync();
        var id = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, id, Club, 0, 1);
        await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, id);
        await MaakStartOuderAsync(id, minuten: 45);

        var batches = await Task.WhenAll(HerstelAsync(), HerstelAsync(), HerstelAsync());

        batches.Count(b => b.Retry.Any(r => r.JobId == id)).Should().Be(1);
    }

    [PostgresFact]
    public async Task Herstel_RaaktAfgerondeEnVerseJobsNiet()
    {
        await SchoonAsync();
        var klaar = Guid.NewGuid();
        var vers = Guid.NewGuid();
        await SyncJobsRepository.CreateAsync(ConnectionString, klaar, Club, 0, 1);
        await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, klaar);
        await SyncJobsRepository.MarkCompletedAsync(ConnectionString, klaar, succeeded: true, errorMessage: null, attempt: 1);
        await MaakStartOuderAsync(klaar, minuten: 120);
        await SyncJobsRepository.CreateAsync(ConnectionString, vers, Club, 0, 1);
        await SyncJobsRepository.TryClaimAttemptAsync(ConnectionString, vers);

        var batch = await HerstelAsync();

        batch.Retry.Select(r => r.JobId).Should().NotContain([klaar, vers]);
        (await StatusAsync(klaar)).Should().Be("succeeded");
        (await StatusAsync(vers)).Should().Be("running");
    }

    [Fact]
    public void HerstelBericht_HeeftDezelfdeVormAlHetBerichtVanDeAdminTrigger()
    {
        var item = new SyncJobRecoveryItem(Guid.NewGuid(), "testclub-syncjobs", -1, 3);

        var gelezen = System.Text.Json.JsonSerializer.Deserialize<SyncJobMessage>(System.Text.Json.JsonSerializer.Serialize(item));

        gelezen.Should().NotBeNull();
        gelezen!.JobId.Should().Be(item.JobId);
        gelezen.ClubCode.Should().Be(item.ClubCode);
        gelezen.WeekOffsetFrom.Should().Be(-1);
        gelezen.WeekOffsetTo.Should().Be(3);
    }

    private static Task<SyncJobRecoveryBatch> HerstelAsync()
        => SyncJobsRepository.RecoverInterruptedAsync(ConnectionString, SyncJobRecoveryPolicy.StaleCutoff(DateTime.UtcNow));

    private static async Task<string> StatusAsync(Guid id)
        => (await SyncJobsRepository.GetLatestOrByIdAsync(ConnectionString, Club, id))!.Status;

    private static async Task MaakStartOuderAsync(Guid id, int minuten)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE public.syncjobs SET startedat = now() - make_interval(mins => @minuten) - interval '2 seconds' WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("minuten", minuten);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
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
