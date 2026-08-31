using Database.Postgres;
using FunctionApp.Postgres.Planner.Repositories;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Npgsql;

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
/// <item><b>Seizoensgrenzen</b> (<c>public.season</c>, #890-vervolg) worden door de buitenste
/// triggers opgezocht via <c>PostgresSeasonHelper</c>; <see cref="RunSyncAsync"/> zelf neemt
/// <c>fromWeekOffset</c>/<c>toWeekOffset</c> als expliciete parameters, exact zoals het origineel.</item>
/// </list>
/// </summary>
internal static class PostgresSyncPipeline
{
    private static readonly HttpClient HttpClient = new();

    internal static async Task RunSyncAsync(
        int fromWeekOffset, int toWeekOffset,
        string sportlinkApiUrl, string sportlinkClientId,
        string clubCode, string connectionString,
        ILogger log)
    {
        var partialFailure = false;
        var orchestrator = new PostgresMergeOrchestrator(connectionString);

        await orchestrator.RecreateStgTableAsync(KnownEntities.Teams);
        try
        {
            await FetchAndStoreTeamsAsync(connectionString, $"{sportlinkApiUrl}/teams?{sportlinkClientId}", clubCode, log);
            log.LogInformation("TEAMS - GET endpoint=/teams");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TEAMS - fetch mislukt");
            partialFailure = true;
        }

        await orchestrator.RecreateStgTableAsync(KnownEntities.Matches);

        log.LogInformation("MATCHES/PROGRAMMA - Fetching weekOffset {From} to {To}", fromWeekOffset, toWeekOffset);
        for (var weekOffset = fromWeekOffset; weekOffset <= toWeekOffset; weekOffset++)
        {
            try
            {
                await FetchAndStoreProgrammaAsync(
                    connectionString, $"{sportlinkApiUrl}/programma?{sportlinkClientId}&weekoffset={weekOffset}", clubCode, log);
                log.LogInformation("MATCHES/PROGRAMMA - GET weekOffset={WeekOffset}", weekOffset);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "MATCHES/PROGRAMMA - fetch mislukt weekOffset={WeekOffset}", weekOffset);
                partialFailure = true;
            }
        }

        var scoreFrom = Math.Min(fromWeekOffset, -2);
        log.LogInformation("MATCHES/UITSLAGEN - Fetching weekOffset {From} to 0", scoreFrom);
        for (var weekOffset = scoreFrom; weekOffset <= 0; weekOffset++)
        {
            try
            {
                await FetchAndStoreUitslagenAsync(
                    connectionString, $"{sportlinkApiUrl}/uitslagen?{sportlinkClientId}&weekoffset={weekOffset}", clubCode, log);
                log.LogInformation("MATCHES/UITSLAGEN - GET weekOffset={WeekOffset}", weekOffset);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "MATCHES/UITSLAGEN - fetch mislukt weekOffset={WeekOffset}", weekOffset);
                partialFailure = true;
            }
        }

        await orchestrator.RecreateStgTableAsync(KnownEntities.MatchDetails);
        var wedstrijdcodes = await PostgresStagingRepository.GetWedstrijdcodesAsync(connectionString, log);
        int mdOk = 0, mdFout = 0;
        foreach (var wedstrijdcode in wedstrijdcodes)
        {
            if (await FetchAndStoreMatchDetailsAsync(
                    connectionString,
                    $"{sportlinkApiUrl}/wedstrijd-informatie?{sportlinkClientId}&wedstrijdcode={wedstrijdcode}",
                    clubCode, log))
            {
                mdOk++;
                log.LogInformation("MATCHDETAILS - GET wedstrijdcode={Code}", wedstrijdcode);
            }
            else
            {
                mdFout++;
                partialFailure = true;
            }
        }
        log.LogInformation("MATCHDETAILS - {Ok} succesvol, {Fout} mislukt van {Total}",
            mdOk, mdFout, wedstrijdcodes.Count);

        await orchestrator.EnsureHisTableAsync(KnownEntities.Teams);
        await orchestrator.MergeStgToHisAsync(KnownEntities.Teams);
        await orchestrator.EnsureHisTableAsync(KnownEntities.Matches);
        await orchestrator.MergeStgToHisAsync(KnownEntities.Matches);
        await orchestrator.EnsureHisTableAsync(KnownEntities.MatchDetails);
        await orchestrator.MergeStgToHisAsync(KnownEntities.MatchDetails);

        // Plannerview (#861/#819): CREATE OR REPLACE VIEW planner.alle_wedstrijden_op_veld_ruw.
        //
        // WAAROM HIER EN NIET IN EEN MIGRATIE. De view selecteert uit his.matches/his.teams, en die
        // tabellen worden niet door een migratie aangemaakt maar dynamisch door
        // PostgresMergeOrchestrator zodra de eerste sync draait (#818). Postgres controleert de
        // gerefereerde relaties al bij CREATE VIEW, dus een migratie zou hard falen met
        // "relation his.matches does not exist" — empirisch bevestigd op een verse database.
        // Dit is het vroegste punt waarop de view wél gemaakt kan worden.
        //
        // ZONDER DEZE AANROEP was de view op een verse installatie volledig afwezig: hij werd
        // alleen door de testsuites aangemaakt. Elk endpoint dat eruit leest
        // (veldbezetting, check-availability, doordeweeks-beschikbaar, herplan-check, auto-plan)
        // faalde daardoor met 42P01 — een gat dat sectie 32 al signaleerde maar dat nog geen
        // eigenaar had.
        //
        // CREATE OR REPLACE is idempotent, dus meelopen op elke sync is veilig en houdt de view
        // bovendien vanzelf gelijk aan PostgresPlannerViewGenerator na een wijziging daar.
        await EnsurePlannerViewAsync(connectionString, log);

        // Teamcanonicalisatie (#889): vult public.teams/public.teamaliassen uit his.teams/his.matches.
        //
        // BEST-EFFORT, met opzet — exact zoals het SQL Server-origineel, dat beide aanroepen met
        // try/catch omgeeft: his.* is op dit punt al gemerged, en een fout in de afgeleide
        // canonicalisatie mag die geslaagde ETL-run niet alsnog laten falen. Dat is bewust een
        // ander soort stap dan MarkeerVervallenGeplandeWedstrijdenAsync hieronder, die juist
        // ONgeguard is.
        await CanonicaliseerBestEffortAsync(connectionString, clubCode, clubCode, log);

        // AllStars FC (#756) heeft geen eigen Sportlink-sync — zijn his.teams-rijen komen uit de
        // demodata-seed, niet uit deze pipeline. Zonder deze aanroep blijft public.teams voor de
        // democlub voor altijd leeg terwijl his.* wel gevuld is: de teamdropdown in de Admin UI zou
        // dan voor de democlub 0 teams tonen. Meelopen op elke echte sync houdt de demodata canoniek
        // zonder een aparte job.
        if (!clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase))
            await CanonicaliseerBestEffortAsync(connectionString, "ALLSTARS", "democlub ALLSTARS", log);

        // Ongeguard, met opzet — zie klasse-doc-comment hierboven.
        await PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync(connectionString, clubCode, log);

        if (!partialFailure)
            await SaveLastSyncTimestampAsync(connectionString, clubCode, log);
        else
            log.LogWarning("Sync gedeeltelijk mislukt — lastsynctimestamp NIET bijgewerkt");
    }

    /// <summary>
    /// Roept de teamcanonicalisatie aan zonder dat een fout de rest van de sync raakt — zelfde
    /// try/catch-vorm als het SQL Server-origineel. <paramref name="omschrijving"/> staat alleen in
    /// de foutmelding, zodat de twee aanroepen (primaire club en democlub) in het log uit elkaar
    /// te houden zijn.
    /// </summary>
    /// <summary>
    /// Maakt of vervangt <c>planner.alle_wedstrijden_op_veld_ruw</c> (#861). Eén bron van waarheid:
    /// de DDL komt uit <see cref="Database.Postgres.PostgresPlannerViewGenerator.CreateView"/>, niet
    /// uit een tweede kopie in een migratiebestand — <c>VeldResolutieDriftTests</c> bewaakt die
    /// generator, en een SQL-kopie ernaast zou stilzwijgend uit de pas kunnen lopen.
    /// <para>
    /// Bewust NIET best-effort. Zonder deze view faalt de halve plannerlaag met een
    /// <c>relation does not exist</c>; dat stil doorlaten zou de sync groen laten melden terwijl de
    /// applicatie erna kapot is. Vergelijk <see cref="CanonicaliseerBestEffortAsync"/>, die juist
    /// wél geguard is omdat een mislukte canonicalisatie de al geslaagde ETL niet ongedaan maakt.
    /// </para>
    /// </summary>
    private static async Task EnsurePlannerViewAsync(string connectionString, ILogger log)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(Database.Postgres.PostgresPlannerViewGenerator.CreateView, conn);
        await cmd.ExecuteNonQueryAsync();
        log.LogInformation("PLANNERVIEW - {View} aangemaakt of bijgewerkt",
            Database.Postgres.PostgresPlannerViewGenerator.ViewName);
    }

    private static async Task CanonicaliseerBestEffortAsync(
        string connectionString, string clubCode, string omschrijving, ILogger log)
    {
        try
        {
            await TeamResolution.TeamCanonicalisatieService.RefreshAsync(connectionString, clubCode, log);
        }
        catch (Exception ex)
        {
            // Nooit de hele sync laten falen op de teamcanonicalisatie (#696) — his.teams/matches
            // zijn al gemerged; de volgende sync probeert het opnieuw.
            log.LogError(ex, "TEAMS CANONICALISATIE - mislukt voor {Omschrijving}", omschrijving);
        }
    }

    private static async Task SaveLastSyncTimestampAsync(string connectionString, string clubCode, ILogger log)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE public.appsettings SET lastsynctimestamp = NOW() WHERE clubcode = @clubcode", connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        await command.ExecuteNonQueryAsync();
        log.LogInformation("lastsynctimestamp bijgewerkt voor club {ClubCode}", clubCode);
    }

    private static async Task FetchAndStoreTeamsAsync(string connectionString, string apiUrl, string clubCode, ILogger log)
    {
        var response = await HttpClient.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var teams = JsonConvert.DeserializeObject<List<Team>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (teams != null)
        {
            log.LogInformation("TEAMS - {Count} gevonden.", teams.Count);
            await PostgresStagingRepository.SaveTeamsAsync(connectionString, teams, clubCode, log);
        }
        else
        {
            log.LogWarning("TEAMS - geen data gevonden.");
        }
    }

    private static async Task FetchAndStoreProgrammaAsync(string connectionString, string apiUrl, string clubCode, ILogger log)
    {
        var response = await HttpClient.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var matches = JsonConvert.DeserializeObject<List<Match>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (matches is { Count: > 0 })
        {
            log.LogInformation("MATCHES/PROGRAMMA - {Count} gevonden.", matches.Count);
            await PostgresStagingRepository.SaveProgrammaAsync(connectionString, matches, clubCode, log);
        }
    }

    private static async Task FetchAndStoreUitslagenAsync(string connectionString, string apiUrl, string clubCode, ILogger log)
    {
        var response = await HttpClient.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var matches = JsonConvert.DeserializeObject<List<Match>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (matches is { Count: > 0 })
        {
            log.LogInformation("MATCHES/UITSLAGEN - {Count} gevonden.", matches.Count);
            await PostgresStagingRepository.MergeUitslagenAsync(connectionString, matches, clubCode, log);
        }
    }

    private static async Task<bool> FetchAndStoreMatchDetailsAsync(string connectionString, string apiUrl, string clubCode, ILogger log)
    {
        try
        {
            var response = await HttpClient.GetAsync(apiUrl);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            try
            {
                var details = JsonConvert.DeserializeObject<MatchDetails>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
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
            log.LogError(ex, "MATCHDETAILS - ophalen mislukt voor {Url}", apiUrl);
            return false;
        }
    }
}
