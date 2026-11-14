using Database.Postgres;
using FunctionApp.Postgres.Planner.Repositories;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Npgsql;
using Planner.Shared.Sync;

namespace FunctionApp.Postgres.Sync;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Sync/SportlinkSyncPipeline.cs</c> (#890) —
/// orkestreert API ophalen → staging → merge, met <see cref="PostgresMergeOrchestrator"/>
/// (#818) voor het schema-/mergewerk in plaats van SQL Server's <c>CreateStagingTable</c> +
/// <c>MergeStgToHis</c> + <c>sp_CreateTargetTableFromSource</c>/<c>sp_MergeStgToHis</c>.
/// <para>
/// <b>Alle drie de nastappen van het origineel staan er inmiddels:</b>
/// </para>
/// <list type="bullet">
/// <item><b>Teamcanonicalisatie</b> (<see cref="TeamResolution.TeamCanonicalisatieService"/>, #889)
/// — twee aanroepen, primaire club en democlub, allebei <b>best-effort</b> (try/catch), exact zoals
/// het origineel: <c>his.*</c> is op dat punt al gemerged en een fout in de afgeleide
/// canonicalisatie mag die geslaagde ETL-run niet alsnog laten falen.</item>
/// <item><b><c>MarkeerVervallenGeplandeWedstrijdenAsync</c></b> (#890, zie
/// <see cref="Planner.Repositories.PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync"/>)
/// wordt — net als het origineel — <b>ONGEGUARD</b> aangeroepen: een fout daar hoort de hele sync
/// te laten falen. Dat verschil met de best-effort canonicalisatie hierboven is opzettelijk.</item>
/// <item><b>Seizoensgrenzen</b> (<c>public.clubseason</c>, #890-vervolg) worden door de buitenste
/// triggers opgezocht via <c>PostgresSeasonHelper</c>; <see cref="RunSyncAsync"/> zelf neemt
/// <c>fromWeekOffset</c>/<c>toWeekOffset</c> als expliciete parameters, exact zoals het origineel.</item>
/// </list>
/// </summary>
internal static partial class PostgresSyncPipeline
{
    internal static readonly HttpClientHandler DataserviceHandler = new() { AllowAutoRedirect = false };
    private static readonly HttpClient HttpClient = new(DataserviceHandler);

    /// <summary>
    /// Draait de volledige synchronisatie en geeft terug of er deelstappen zijn mislukt.
    /// <para>
    /// <b>#1081: de uitkomst wordt teruggegeven in plaats van alleen gelogd.</b> Tot dan eindigde
    /// een run met mislukte deelstappen met een waarschuwing in het log en verder niets — de
    /// aanroeper kon het verschil tussen "geslaagd" en "gedeeltelijk mislukt" niet zien, en de
    /// timerinvocatie werd dus als Success gerapporteerd terwijl er acht dagen niets werd
    /// bijgewerkt (#1077).
    /// </para>
    /// </summary>
    internal static async Task<SyncUitkomst> RunSyncAsync(
        int fromWeekOffset, int toWeekOffset,
        string sportlinkApiUrl, string sportlinkClientId,
        string clubCode, string connectionString,
        ILogger log, DateOnly? reconciliatieOndergrens = null, SyncLogWriter? syncLog = null)
    {
        await using var stagingLease = await PostgresSyncStagingLease.TryAcquireAsync(connectionString)
            ?? throw new SyncAlreadyRunningException();

        var uitkomst = new SyncUitkomst();
        // #1779: één herhaalbudget voor alle dataservice-GET's van deze run (begrenst een brede storing).
        var budget = new HerhaalBudget();
        var orchestrator = new PostgresMergeOrchestrator(connectionString);

        await orchestrator.RecreateStgTableAsync(KnownEntities.Teams);
        await SyncLogReporting.BeginPhaseAsync(syncLog, "teams");
        int? teamsFetched = null;
        var teamsFailed = uitkomst.Registreer(SyncUitkomst.FaseTeams,
            await FetchTeamsPhaseAsync(orchestrator, connectionString, sportlinkApiUrl, sportlinkClientId, clubCode, budget, log, syncLog,
                count => teamsFetched = count));

        await orchestrator.RecreateStgTableAsync(KnownEntities.Matches);
        await SyncLogReporting.BeginPhaseAsync(syncLog, "programma");
        var programmaAfgekapt = false;
        var programmaFetched = 0;
        var programmaFailedCount = 0;
        var programmaFailed = uitkomst.Registreer(SyncUitkomst.FaseProgramma,
            await FetchProgrammaPhaseAsync(connectionString, sportlinkApiUrl, sportlinkClientId, clubCode, fromWeekOffset, toWeekOffset, budget, log, syncLog,
                count => programmaFetched += count, () => programmaAfgekapt = true, () => programmaFailedCount++));
        await SyncLogReporting.BeginPhaseAsync(syncLog, "uitslagen");
        var uitslagenFetched = 0;
        var uitslagenFailedCount = 0;
        var uitslagenFailed = uitkomst.Registreer(SyncUitkomst.FaseUitslagen,
            await FetchUitslagenPhaseAsync(connectionString, sportlinkApiUrl, sportlinkClientId, clubCode, fromWeekOffset, budget, log, syncLog,
                count => uitslagenFetched += count, () => uitslagenFailedCount++));
        // #1547: een gespeelde wedstrijd komt alleen nog via /uitslagen binnen, zonder veld/datum/team.
        await PostgresStagingRepository.VulUitslagRijenAanUitHisAsync(connectionString, clubCode);
        var matchesFailed = programmaFailed || uitslagenFailed;

        await orchestrator.RecreateStgTableAsync(KnownEntities.MatchDetails);
        await SyncLogReporting.BeginPhaseAsync(syncLog, "matchdetails");
        var matchdetailsFetched = 0;
        var matchdetailsFailedCount = 0;
        uitkomst.Registreer(SyncUitkomst.FaseMatchdetails,
            await FetchMatchDetailsPhaseAsync(connectionString, sportlinkApiUrl, sportlinkClientId, clubCode, budget, log, syncLog,
                count => matchdetailsFetched += count, count => matchdetailsFailedCount = count));

        var merges = await MergeAllEntitiesAsync(orchestrator);
        await SyncLogReporting.CompletePhaseAsync(syncLog, "teams", SyncLogReporting.StatusForPartialFailure(teamsFailed),
            fetched: teamsFetched, added: merges.Teams.Added, updated: merges.Teams.Updated,
            failed: teamsFailed ? 1 : 0, reasonCode: SyncLogReporting.ReasonForFailure(teamsFailed, "source_failed"));
        await SyncLogReporting.CompletePhaseAsync(syncLog, "programma", SyncLogReporting.StatusForPartialFailure(programmaFailed),
            fetched: programmaFetched, added: merges.Matches.Added, updated: merges.Matches.Updated,
            failed: programmaFailedCount,
            reasonCode: SyncLogReporting.ProgrammaReason(programmaAfgekapt, programmaFailed));
        // Programme and results share stg.matches and one merge into his.matches; do not split the
        // merge counters between the two provider fetch phases.
        await SyncLogReporting.CompletePhaseAsync(syncLog, "uitslagen", SyncLogReporting.StatusForPartialFailure(uitslagenFailed),
            fetched: uitslagenFetched, failed: uitslagenFailedCount,
            reasonCode: SyncLogReporting.ReasonForFailure(uitslagenFailed, "partial_failure"));
        var matchdetailsFailed = uitkomst.MislukteFases.Contains(SyncUitkomst.FaseMatchdetails);
        await SyncLogReporting.CompletePhaseAsync(syncLog, "matchdetails", SyncLogReporting.StatusForPartialFailure(matchdetailsFailed),
            fetched: matchdetailsFetched, failed: matchdetailsFailedCount,
            added: merges.MatchDetails.Added, updated: merges.MatchDetails.Updated,
            reasonCode: SyncLogReporting.ReasonForFailure(matchdetailsFailed, "partial_failure"));

        // Reconciliatie (#1193): "Sportlink is de waarheid altijd" — his-rijen die niet meer in de
        // zojuist geladen stg-snapshot voorkomen worden gemarkeerd als verwijderd (mta_deleted),
        // nooit hard verwijderd. BEST-EFFORT, met opzet, om dezelfde reden als de
        // teamcanonicalisatie hieronder: his.* is op dit punt al gemerged, dus een fout hier mag de
        // al geslaagde ETL-run niet alsnog laten falen. Draait vóór de plannerview/canonicalisatie
        // zodat die stroomafwaartse stappen al met de opgeschoonde data werken.
        await SyncLogReporting.BeginPhaseAsync(syncLog, "reconciliatie");
        var reconciliationFailed = await ReconcileVerdwenenAsync(orchestrator, clubCode, teamsFailed, matchesFailed,
            reconciliatieOndergrens ?? EersteTeReconcilierenDatum(VandaagInNederland()), log);
        var reconciliationSkipped = teamsFailed || matchesFailed;
        var allReconciliationSkipped = teamsFailed && matchesFailed;
        await SyncLogReporting.CompletePhaseAsync(syncLog, "reconciliatie",
            SyncLogReporting.ReconciliationStatus(allReconciliationSkipped, reconciliationSkipped, reconciliationFailed),
            reasonCode: SyncLogReporting.ReconciliationReason(reconciliationSkipped, reconciliationFailed));

        await RondAfNaMergeAsync(connectionString, clubCode, log, syncLog);

        if (!uitkomst.GedeeltelijkMislukt)
            await SaveLastSyncTimestampAsync(connectionString, clubCode, log);
        else
            log.LogWarning("Sync gedeeltelijk mislukt — lastsynctimestamp NIET bijgewerkt");

        return uitkomst;
    }

    private static async Task<bool> FetchTeamsPhaseAsync(
        PostgresMergeOrchestrator orchestrator, string connectionString,
        string sportlinkApiUrl, string sportlinkClientId, string clubCode, HerhaalBudget budget, ILogger log, SyncLogWriter? syncLog, Action<int> setFetched)
    {
        try
        {
            var volledig = await FetchAndStoreTeamsAsync(connectionString, $"{sportlinkApiUrl}/teams?{sportlinkClientId}", clubCode, log, budget: budget,
                fetched: async count => { setFetched(count); await SyncLogReporting.ReportProgressAsync(syncLog, "teams", fetched: count); });
            log.LogInformation("TEAMS - GET endpoint=/teams");
            // #1558 R1-F1: een lege of ontbrekende respons is geen geldige snapshot; dat telt als
            // mislukte fetch, zodat reconciliatie niet alle teams als verdwenen markeert.
            return !volledig;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TEAMS - fetch mislukt");
            return true;
        }
    }

    private static async Task<bool> FetchProgrammaPhaseAsync(
        string connectionString, string sportlinkApiUrl, string sportlinkClientId, string clubCode,
        int fromWeekOffset, int toWeekOffset, HerhaalBudget budget, ILogger log, SyncLogWriter? syncLog,
        Func<int, int> addFetched, Action? markTruncated, Action markFailed)
    {
        var partialFailure = false;

        log.LogInformation("MATCHES/PROGRAMMA - Fetching weekOffset {From} to {To}", fromWeekOffset, toWeekOffset);
        for (var weekOffset = fromWeekOffset; weekOffset <= toWeekOffset; weekOffset++)
        {
            try
            {
                var volledig = await FetchAndStoreProgrammaAsync(
                    connectionString,
                    $"{sportlinkApiUrl}/programma?{sportlinkClientId}&weekoffset={weekOffset}&aantaldagen={ProgrammaAfkapping.VensterDagen}",
                    clubCode, log, budget: budget, fetched: async count =>
                    {
                        var fetchedTotal = addFetched(count);
                        await SyncLogReporting.ReportProgressAsync(syncLog, "programma", fetched: fetchedTotal,
                            progress: weekOffset - fromWeekOffset + 1, total: toWeekOffset - fromWeekOffset + 1);
                    });
                log.LogInformation("MATCHES/PROGRAMMA - GET weekOffset={WeekOffset}", weekOffset);
                // #1915: een afgekapt venster is een onvolledige snapshot — nooit reconciliëren op wat ontbreekt.
                if (!volledig) { partialFailure = true; markTruncated?.Invoke(); markFailed(); }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "MATCHES/PROGRAMMA - fetch mislukt weekOffset={WeekOffset}", weekOffset);
                partialFailure = true;
                markFailed();
                await SyncLogReporting.ReportProgressAsync(syncLog, "programma", fetched: addFetched(0),
                    progress: weekOffset - fromWeekOffset + 1, total: toWeekOffset - fromWeekOffset + 1);
            }
        }

        return partialFailure;
    }

    private static async Task<bool> FetchUitslagenPhaseAsync(
        string connectionString, string sportlinkApiUrl, string sportlinkClientId, string clubCode,
        int fromWeekOffset, HerhaalBudget budget, ILogger log, SyncLogWriter? syncLog, Func<int, int> addFetched, Action markFailed)
    {
        var partialFailure = false;

        var scoreFrom = Math.Min(fromWeekOffset, -2);
        log.LogInformation("MATCHES/UITSLAGEN - Fetching weekOffset {From} to 0", scoreFrom);
        for (var weekOffset = scoreFrom; weekOffset <= 0; weekOffset++)
        {
            try
            {
                await FetchAndStoreUitslagenAsync(
                    connectionString, $"{sportlinkApiUrl}/uitslagen?{sportlinkClientId}&weekoffset={weekOffset}", clubCode, log, budget: budget,
                    fetched: async count =>
                    {
                        var fetchedTotal = addFetched(count);
                        await SyncLogReporting.ReportProgressAsync(syncLog, "uitslagen", fetched: fetchedTotal,
                            progress: weekOffset - scoreFrom + 1, total: -scoreFrom + 1);
                    });
                log.LogInformation("MATCHES/UITSLAGEN - GET weekOffset={WeekOffset}", weekOffset);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "MATCHES/UITSLAGEN - fetch mislukt weekOffset={WeekOffset}", weekOffset);
                partialFailure = true;
                markFailed();
                await SyncLogReporting.ReportProgressAsync(syncLog, "uitslagen", fetched: addFetched(0),
                    progress: weekOffset - scoreFrom + 1, total: -scoreFrom + 1);
            }
        }

        return partialFailure;
    }

    private static async Task<bool> FetchMatchDetailsPhaseAsync(
        string connectionString, string sportlinkApiUrl, string sportlinkClientId, string clubCode, HerhaalBudget budget, ILogger log, SyncLogWriter? syncLog,
        Action<int> addFetched, Action<int> setFailed)
    {
        var wedstrijdcodes = await PostgresStagingRepository.GetWedstrijdcodesAsync(connectionString, log);
        int mdOk = 0, mdFout = 0;
        foreach (var wedstrijdcode in wedstrijdcodes)
        {
            if (await FetchAndStoreMatchDetailsAsync(
                    connectionString,
                    $"{sportlinkApiUrl}/wedstrijd-informatie?{sportlinkClientId}&wedstrijdcode={wedstrijdcode}",
                    clubCode, log, budget: budget, fetchedCount: addFetched))
            {
                mdOk++;
                log.LogInformation("MATCHDETAILS - GET wedstrijdcode={Code}", wedstrijdcode);
            }
            else
            {
                mdFout++;
                setFailed(mdFout);
                // De foutregel in FetchAndStoreMatchDetailsAsync noemt bewust geen URL meer (#1200);
                // de wedstrijdcode is hier wél veilig te loggen en houdt de fout herleidbaar.
                log.LogWarning("MATCHDETAILS - mislukt voor wedstrijdcode={Code}", wedstrijdcode);
            }
            await SyncLogReporting.ReportProgressAsync(syncLog, "matchdetails", failed: mdFout,
                progress: mdOk + mdFout, total: wedstrijdcodes.Count);
        }
        log.LogInformation("MATCHDETAILS - {Ok} succesvol, {Fout} mislukt van {Total}",
            mdOk, mdFout, wedstrijdcodes.Count);

        return mdFout > 0;
    }

    private static async Task<((int Added, int Updated) Teams, (int Added, int Updated) Matches, (int Added, int Updated) MatchDetails)> MergeAllEntitiesAsync(PostgresMergeOrchestrator orchestrator)
    {
        await orchestrator.EnsureHisTableAsync(KnownEntities.Teams);
        var teams = await orchestrator.MergeStgToHisWithCountsAsync(KnownEntities.Teams);
        await orchestrator.EnsureHisTableAsync(KnownEntities.Matches);
        var matches = await orchestrator.MergeStgToHisWithCountsAsync(KnownEntities.Matches);
        await orchestrator.EnsureHisTableAsync(KnownEntities.MatchDetails);
        var details = await orchestrator.MergeStgToHisWithCountsAsync(KnownEntities.MatchDetails);
        return (teams, matches, details);
    }

    /// <summary>
    /// Reconciliatie na de merge (#1193): markeert his-rijen die niet meer in de zojuist geladen
    /// stg-snapshot voorkomen als verwijderd (<c>mta_deleted</c>). BEST-EFFORT per entiteit, met
    /// opzet — zelfde afweging als <see cref="CanonicaliseerBestEffortAsync"/>: his.* is op dit punt
    /// al succesvol gemerged, dus een fout in deze afgeleide opruimstap mag die geslaagde ETL-run
    /// niet alsnog laten falen.
    /// <para>
    /// <b>Alleen wanneer de bijbehorende fetch-fase zonder fouten verliep</b>
    /// (<paramref name="teamsFailed"/>/<paramref name="matchesFailed"/>) — anders kan een
    /// lege/onvolledige stg-snapshot legitieme his-rijen ten onrechte als "verdwenen bij Sportlink"
    /// laten lijken terwijl het gewoon een mislukte aanroep was. Bewust twee losse vlaggen in plaats
    /// van de gecombineerde <c>partialFailure</c>: een mislukte matchdetails-fetch (die wél in
    /// <c>partialFailure</c> meetelt) mag de matches- en teams-reconciliatie niet onnodig blokkeren.
    /// </para>
    /// <para>
    /// <c>matchdetails</c> reconcilieert bewust NIET mee: <c>/wedstrijd-informatie</c> wordt per
    /// wedstrijdcode los opgehaald (<see cref="FetchMatchDetailsPhaseAsync"/>), en een individuele
    /// fetch-fout voor één wedstrijd telt niet mee in <paramref name="matchesFailed"/>. Zonder die
    /// garantie zou een reconciliatie hier een detailrij van een wél nog bestaande wedstrijd onterecht
    /// als verdwenen kunnen markeren, puur door een transiënte netwerkfout bij die ene aanroep.
    /// </para>
    /// </summary>
    // De ondergrensregel is tier-onafhankelijk en staat in Planner.Shared (#1558); deze wrappers
    // houden de bestaande aanroepen en tests intact.
    internal static DateOnly EersteTeReconcilierenDatum(DateOnly vandaag) =>
        global::Planner.Shared.Sync.ReconciliatieOndergrens.EersteTeReconcilierenDatum(vandaag);

    private static DateOnly VandaagInNederland() => VandaagInNederland(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById);

    internal static DateOnly VandaagInNederland(DateTime utcNu, Func<string, TimeZoneInfo> zoekTijdzone) =>
        global::Planner.Shared.Sync.ReconciliatieOndergrens.VandaagInNederland(utcNu, zoekTijdzone);

    // #1779: alle dataservice-GET's lopen via SportlinkDataserviceHttp (begrensde herhaling op
    // 5xx/429/time-out, runbreed budget); zonder budget (tests) geldt alleen de grens per aanroep.
    internal static async Task<bool> FetchAndStoreTeamsAsync(string connectionString, string apiUrl, string clubCode, ILogger log,
        HttpClient? httpClient = null, TimeSpan? herhaalWachttijd = null, HerhaalBudget? budget = null, Func<int, Task>? fetched = null)
    {
        var json = await SportlinkDataserviceHttp.HaalTekstAsync(
            httpClient ?? HttpClient, apiUrl, budget ?? new HerhaalBudget(), "TEAMS", log, herhaalWachttijd);
        var teams = JsonConvert.DeserializeObject<List<Team>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (teams is not null) await (fetched?.Invoke(teams.Count) ?? Task.CompletedTask);
        if (teams is { Count: > 0 })
        {
            log.LogInformation("TEAMS - {Count} gevonden.", teams.Count);
            await PostgresStagingRepository.SaveTeamsAsync(connectionString, teams, clubCode, log);
        }
        else
        {
            log.LogWarning("TEAMS - geen data gevonden.");
        }
        return global::Planner.Shared.Sync.ReconciliatieOndergrens.IsVolledigeTeamsSnapshot(teams?.Count);
    }

    /// <summary>
    /// Haalt één <c>/programma</c>-venster op en slaat het op in staging. Raakt het venster Sportlinks plafond van 100
    /// rijen, dan volgt een gesplitste ophaling in thuis en uit (#1915, <see cref="ProgrammaAfkapping"/>).
    /// </summary>
    /// <returns><c>false</c> als het venster ook na splitsing afgekapt is: dan ontbreken er wedstrijden en mag de
    /// aanroeper niet reconciliëren.</returns>
    internal static async Task<bool> FetchAndStoreProgrammaAsync(string connectionString, string apiUrl, string clubCode, ILogger log,
        HttpClient? httpClient = null, TimeSpan? herhaalWachttijd = null, HerhaalBudget? budget = null, Func<int, Task>? fetched = null)
    {
        var herhaalBudget = budget ?? new HerhaalBudget();
        var ophaling = await ProgrammaAfkapping.HaalVolledigAsync<Match>(apiUrl, async url =>
        {
            var json = await SportlinkDataserviceHttp.HaalTekstAsync(
                httpClient ?? HttpClient, url, herhaalBudget, "MATCHES/PROGRAMMA", log, herhaalWachttijd);
            return JsonConvert.DeserializeObject<List<Match>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore })
                ?? new List<Match>();
        }, () => PostgresStagingRepository.GetTeamcodesAsync(connectionString));
        if (ophaling.Gesplitst)
            log.LogInformation("MATCHES/PROGRAMMA - venster raakte het plafond van {Plafond} rijen; opgehaald {Wijze} ({Count} rijen).",
                ProgrammaAfkapping.Plafond, ophaling.PerTeam ? "per team" : "als thuis + uit", ophaling.Rijen.Count);
        await (fetched?.Invoke(ophaling.Rijen.Count) ?? Task.CompletedTask);
        if (!ophaling.Volledig)
            log.LogWarning("MATCHES/PROGRAMMA - venster is ook na splitsing (thuis/uit, per team) afgekapt op {Plafond} rijen; sync telt als onvolledig.",
                ProgrammaAfkapping.Plafond);
        if (ophaling.Rijen.Count > 0)
        {
            log.LogInformation("MATCHES/PROGRAMMA - {Count} gevonden.", ophaling.Rijen.Count);
            await PostgresStagingRepository.SaveProgrammaAsync(connectionString, ophaling.Rijen.ToList(), clubCode, log);
        }
        return ophaling.Volledig;
    }

    internal static async Task FetchAndStoreUitslagenAsync(string connectionString, string apiUrl, string clubCode, ILogger log,
        HttpClient? httpClient = null, TimeSpan? herhaalWachttijd = null, HerhaalBudget? budget = null, Func<int, Task>? fetched = null)
    {
        var json = await SportlinkDataserviceHttp.HaalTekstAsync(
            httpClient ?? HttpClient, apiUrl, budget ?? new HerhaalBudget(), "MATCHES/UITSLAGEN", log, herhaalWachttijd);
        var matches = JsonConvert.DeserializeObject<List<Match>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (matches is not null) await (fetched?.Invoke(matches.Count) ?? Task.CompletedTask);
        if (matches is { Count: > 0 })
        {
            log.LogInformation("MATCHES/UITSLAGEN - {Count} gevonden.", matches.Count);
            await PostgresStagingRepository.MergeUitslagenAsync(connectionString, matches, clubCode, log);
        }
    }

    // Retourneert true bij succes, false bij elke fout — zodat de caller partialFailure kan bijhouden.
    // internal + optionele httpClient: zelfde testbaarheid als de SQL Server-tegenhanger (#476),
    // nodig voor de logregressietests van #1200. Standaard blijft de static HttpClient in gebruik.
    internal static async Task<bool> FetchAndStoreMatchDetailsAsync(
        string connectionString, string apiUrl, string clubCode, ILogger log,
        HttpClient? httpClient = null, TimeSpan? herhaalWachttijd = null, HerhaalBudget? budget = null, Action<int>? fetchedCount = null)
    {
        try
        {
            // #1779: begrensde herhaling op 5xx/429/time-out (idempotente GET); logt nooit de URL.
            var json = await SportlinkDataserviceHttp.HaalTekstAsync(
                httpClient ?? HttpClient, apiUrl, budget ?? new HerhaalBudget(), "MATCHDETAILS", log, herhaalWachttijd);
            try
            {
                var details = JsonConvert.DeserializeObject<MatchDetails>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                fetchedCount?.Invoke(details is null ? 0 : 1);
                if (details != null)
                    await PostgresStagingRepository.SaveMatchDetailsAsync(connectionString, details, clubCode, log);
                else
                    log.LogWarning("MATCHDETAILS - geen data gevonden.");
            }
            catch (JsonSerializationException ex)
            {
                log.LogError(ex, "MATCHDETAILS - JSON-deserialisatiefout ({ErrorType})", ex.GetType().Name);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // NOOIT de apiUrl loggen (#1200): Sportlink authenticeert op de dataservice via de
            // queryparameter clientId, dus de aanroep-URL ís een secret. Vaste metadata + het
            // fouttype geven de operator hetzelfde diagnostische houvast; de wedstrijdcode komt
            // uit de waarschuwing in FetchMatchDetailsPhaseAsync. Zelfde stijl als #436.
            log.LogError(ex, "MATCHDETAILS - ophalen mislukt ({ErrorType}) endpoint=/wedstrijd-informatie",
                ex.GetType().Name);
            return false;
        }
    }
}
