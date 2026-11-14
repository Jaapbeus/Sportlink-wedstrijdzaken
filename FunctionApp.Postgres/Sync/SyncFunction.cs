using Planner.Endpoints.Admin;
using Planner.Shared.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Infrastructure;

namespace FunctionApp.Postgres.Sync;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Function1.cs</c> (#890).
/// <para>
/// <b>Seizoensgrenzen (<c>dbo.ClubSeason</c>)</b> zijn vertaald naar <c>public.clubseason</c> (migratie
/// 043, #1715; daarvoor <c>public.season</c>, migratie 008, #890) — zie <see cref="PostgresSeasonHelper"/>. Zowel de standaardsync (einde seizoen) als
/// de reset-modus (<c>?reset=true&amp;season=</c>, seizoensstart) gebruiken nu de echte tabel in
/// plaats van een geraden of geweigerde waarde.
/// </para>
/// </summary>
public static class SyncFunction
{
    [Function("PostgresFetchAndStoreApiData")]
    public static async Task Run([TimerTrigger("%FETCH_SCHEDULE%")] TimerInfo myTimer, FunctionContext context)
    {
        var log = context.GetLogger("PostgresFetchAndStoreApiData");
        log.LogInformation("Postgres-tier sync uitgevoerd om: {Now}", DateTime.UtcNow);

        if (!EgressGuard.ExternalIntegrationsAllowed())
        {
            log.LogInformation("EgressGuard: uitgaande integraties geblokkeerd buiten productie — sync overgeslagen (#857).");
            return;
        }

        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            await SyncJobsRepository.HerstelOnderbrokenAsync(log); // #1839: herstel onderbroken jobs; loopt mee, geen eigen timer
            await PostgresAppSettings.LoadSettingsAsync(log);
            var clubCode = PostgresAppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubcode' ontbreekt in public.appsettings.");
            var uitkomst = await RunTrackedSyncAsync(async () =>
            {
                var toWeekOffset = await PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync(log);
                return (-1, toWeekOffset);
            }, log, clubCode, () => PostgresSeasonHelper.EnsureSeasonsAsync(log));

            // #1081: een run met mislukte deelstappen mag niet als Success eindigen. Het
            // invocatie-overzicht in de portal toonde acht dagen 'Success 8 · Error 0' terwijl er
            // niets werd bijgewerkt (#1077) — een meetinstrument dat de verkeerde waarde geeft is
            // erger dan geen meetinstrument. Alle werk is op dit punt al gedaan; de uitzondering
            // markeert alleen de invocatie. Er staat geen retry-policy op deze functie, dus dit
            // leidt niet tot een herhaalde run van vijf minuten.
            // #1779: getypeerde uitzondering met faseoverzicht, zodat de foutrapportage een eigen
            // categorie toont in plaats van "Configuratie/status".
            uitkomst.GooiAlsMislukt();
        }
        catch (SyncAlreadyRunningException)
        {
            log.LogInformation("Synchronization skipped because another run owns the shared staging area.");
        }
        catch (Exception ex)
        {
            // Loggen én opnieuw gooien: zonder de rethrow slikt deze catch de uitzondering op en
            // rapporteert de host de invocatie als Success (#1081).
            log.LogError(ex, "PostgresFetchAndStoreApiData fout");
            // #1268: zelfde automatische foutrapportage als de SQL Server-tier al had. Bewust vóór
            // de rethrow — erna is deze regel onbereikbaar. RapporteerAsync gooit zelf nooit: alle
            // fouten in de rapportage worden daar afgevangen en gelogd, dus de rethrow hieronder
            // levert altijd de oorspronkelijke uitzondering af bij de host.
            await FoutRapportage.RapporteerAsync(ex, "PostgresFetchAndStoreApiData", log);
            throw;
        }
    }

    /// <summary>
    /// Default (geen params): vorige week t/m einde seizoen.
    /// Reset mode: GET /api/postgres/sync-matches?reset=true&amp;season=2024 — downloadt alle
    /// wedstrijden vanaf de start van het opgegeven seizoensjaar t/m het einde van het huidige
    /// seizoen. Zelfde gedrag als een ontbrekende/onparseerbare <c>season</c>-param bij het
    /// SQL Server-origineel: valt dan stil terug op de standaardmodus (fromWeekOffset = -1).
    /// <para>
    /// <b>Autorisatie (#1350):</b> Easy Auth + rol <c>admin</c> via <see cref="AdminEndpoint.ExecuteAsync"/>,
    /// zoals elk ander beheerendpoint. Tot #1350 was dit het enige endpoint achter een Azure
    /// Function-<em>master key</em> (<c>AuthorizationLevel.Admin</c>): één statisch geheim zonder
    /// identiteit of audittrail, dat bovendien de volledige Function App beheert. Er bestond geen
    /// geautomatiseerde aanroeper die van die sleutel afhing — de nachtelijke sync is een
    /// timer-trigger in hetzelfde proces, en de deploy-smoketest bewees juist dat een key
    /// <em>geen</em> toegang geeft. De clubcode uit de wrapper wordt bewust genegeerd: een sync
    /// geldt altijd de primaire club uit <c>public.appsettings</c>, nooit de democlub uit de
    /// GUI-clubswitcher.
    /// </para>
    /// </summary>
    [Function("PostgresSyncMatchesHttp")]
    public static Task<IActionResult> SyncMatchesHttp(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "postgres/sync-matches")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("PostgresSyncMatchesHttp");
        return AdminEndpoint.ExecuteAsync(req, log, "handmatige Sportlink-sync",
            async _ =>
            {
                log.LogInformation("HTTP trigger PostgresSyncMatchesHttp uitgevoerd om: {Now}", DateTime.UtcNow);

                if (UitgaandVerkeerCore.Blokkeer(EgressGuard.ExternalIntegrationsAllowed) is { } egressAntwoord) return egressAntwoord;

                var isReset = string.Equals(req.Query["reset"], "true", StringComparison.OrdinalIgnoreCase);
                string? seasonParam = req.Query["season"];

                // Eigen catch naast de wrapper: de automatische foutrapportage (#1268) hoort bij dit
                // endpoint en niet bij de generieke 500 van de wrapper.
                try
                {
                    var venster = (From: -1, To: 0);
                    var uitkomst = await RunTrackedSyncAsync(async () =>
                    {
                        var toWeekOffset = await PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync(log);
                        var fromWeekOffset = -1;
                        if (isReset && int.TryParse(seasonParam, out var seasonStartYear))
                        {
                            fromWeekOffset = await PostgresSeasonHelper.GetSeasonStartWeekOffsetAsync(seasonStartYear, log);
                            log.LogInformation("Reset mode: season {Year}, weekOffset {From} to {To}",
                                seasonStartYear, fromWeekOffset, toWeekOffset);
                        }
                        else
                            log.LogInformation("Default mode: weekOffset {From} to {To}", fromWeekOffset, toWeekOffset);
                        venster = (fromWeekOffset, toWeekOffset);
                        return venster;
                    }, log, beforeWindow: () => PostgresSeasonHelper.EnsureSeasonsAsync(log));
                    return SyncMatchesAntwoordCore.Bepaal(uitkomst, venster.From, venster.To,
                        $"Sync voltooid. WeekOffset-bereik: {venster.From} tot {venster.To}.");
                }
                catch (SyncAlreadyRunningException)
                {
                    return new ObjectResult(new { status = "already running", message = SyncAlreadyRunningException.SafeMessage })
                    { StatusCode = StatusCodes.Status409Conflict };
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "PostgresSyncMatchesHttp fout");
                    // #1268: tegenhanger van de SQL Server-tier (FunctionApp/Function1.cs, SyncMatchesHttp).
                    await FoutRapportage.RapporteerAsync(ex, "PostgresSyncMatchesHttp", log);
                    return new StatusCodeResult(500);
                }
            });
    }

    /// <summary>Geeft de uitkomst met de mislukte fases terug (#1081, #1779).</summary>
    internal static async Task<SyncUitkomst> RunConfiguredSyncAsync(int fromWeekOffset, int toWeekOffset,
        ILogger log, SyncLogWriter? syncLog = null, PostgresSyncStagingLease? acquiredLease = null)
    {
        if (syncLog is null)
            return await RunTrackedSyncAsync(fromWeekOffset, toWeekOffset, log);

        await PostgresAppSettings.LoadSettingsAsync(log);
        var clubCode = PostgresAppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubcode' ontbreekt in public.appsettings — sync kan niet doorgaan.");
        await SyncLogReporting.BeginPhaseAsync(syncLog, "configuratie");

        var connectionString = PostgresDatabaseConfig.ConnectionString;
        var (sportlinkApiUrl, sportlinkClientId) = await GetSportlinkConfigAsync(connectionString, clubCode);
        if (!SportlinkDataserviceBasisUrl.TryGetCanonical(sportlinkApiUrl, out var canonicalSportlinkApiUrl))
        {
            log.LogError("Sportlink data-service base URL is invalid.");
            await SyncLogReporting.CompletePhaseAsync(syncLog, "configuratie", "failed", reasonCode: "configuration_missing");
            return SyncUitkomst.Configuratie();
        }

        await SyncLogReporting.CompletePhaseAsync(syncLog, "configuratie", "succeeded");

        return await PostgresSyncPipeline.RunSyncAsync(
            fromWeekOffset, toWeekOffset,
            canonicalSportlinkApiUrl, $"clientId={sportlinkClientId}",
            clubCode, connectionString, log, syncLog: syncLog, acquiredLease: acquiredLease);
    }

    private static async Task<SyncUitkomst> RunTrackedSyncAsync(int fromWeekOffset, int toWeekOffset, ILogger log, string? loadedClubCode = null)
        => await RunTrackedSyncAsync(() => Task.FromResult((fromWeekOffset, toWeekOffset)), log, loadedClubCode);

    internal static async Task<SyncUitkomst> RunTrackedSyncAsync(
        Func<Task<(int FromWeekOffset, int ToWeekOffset)>> buildWindow, ILogger log,
        string? loadedClubCode = null, Func<Task>? beforeWindow = null,
        string? connectionStringOverride = null)
    {
        if (loadedClubCode is null)
            await PostgresAppSettings.LoadSettingsAsync(log);
        var clubCode = loadedClubCode ?? PostgresAppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubcode' ontbreekt in public.appsettings.");
        var connectionString = connectionStringOverride ?? PostgresDatabaseConfig.ConnectionString;
        await using var stagingLease = await PostgresSyncStagingLease.TryAcquireAsync(connectionString)
            ?? throw new SyncAlreadyRunningException();
        if (beforeWindow is not null)
            await beforeWindow();
        var (fromWeekOffset, toWeekOffset) = await buildWindow();
        return await SyncJobRunCoordinator.RegisterClaimAndRunAsync(
            () => SyncJobsRepository.GetOrCreateActiveAsync(connectionString, Guid.NewGuid(), clubCode, fromWeekOffset, toWeekOffset),
            id => SyncJobsRepository.TryClaimAttemptAsync(connectionString, id),
            new SyncJobRunServices(entry => SyncLogRepository.UpsertAsync(clubCode, entry),
            async () => { await SyncLogRepository.CleanupAllAsync(DateTime.UtcNow); },
            (id, outcome, attempt, incomplete) => SyncJobsRepository.MarkOutcomeAsync(id, outcome, attempt, incomplete),
            (id, succeeded, error, attempt) => SyncJobsRepository.MarkCompletedAsync(id, succeeded, error, attempt),
            writer => RunConfiguredSyncAsync(fromWeekOffset, toWeekOffset, log, writer, stagingLease),
            global::Planner.Endpoints.Sync.SyncQueuedJobCore.SafeFailureMessage));
    }

    private static async Task<(string apiUrl, string clientId)> GetSportlinkConfigAsync(string connectionString, string clubCode)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT sportlinkapiurl, sportlinkclientid FROM public.appsettings WHERE clubcode = @clubcode", connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return (string.Empty, string.Empty);
        return (reader.GetString(0), reader.GetString(1));
    }
}
