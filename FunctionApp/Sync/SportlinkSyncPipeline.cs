using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared.Sync;
using SportlinkFunction.Sync;
using static SportlinkFunction.SystemUtilities;

namespace SportlinkFunction;

/// <summary>
/// Orkestreert de volledige Sportlink-sync: API ophalen → staging → merge → timestamp.
/// Extracted uit Function1.cs (#466).
/// </summary>
internal static class SportlinkSyncPipeline
{
    internal static readonly HttpClientHandler DataserviceHandler = new() { AllowAutoRedirect = false };
    private static readonly HttpClient _client = new(DataserviceHandler);
    private const string AllstarsClubCode = "ALLSTARS";

    // Mislukte fase: LastSyncTimestamp wordt NIET opgeslagen. (#438, #464)
    // Retourneert de uitkomst (#1138, #1779) zodat SyncJobProcessor de job-status correct kan zetten
    // — voorheen kreeg de aanroeper (AdminSyncFunction's fire-and-forget Task.Run) dit nooit te zien.
    internal static async Task<SyncUitkomst> RunSyncAsync(
        int fromWeekOffset, int toWeekOffset,
        string sportlinkApiUrl, string sportlinkClientId,
        ILogger log)
        => await RunSyncWithSeasonWindowAsync(
            () => Task.FromResult((fromWeekOffset, toWeekOffset)), sportlinkApiUrl, sportlinkClientId, log);

    /// <summary>
    /// Builds a default or manual window only after the shared-staging lease and per-club season
    /// rollover are complete. Queued jobs use <see cref="RunSyncAsync"/> with their stored offsets.
    /// </summary>
    internal static async Task<SyncUitkomst> RunSyncWithSeasonWindowAsync(
        Func<Task<(int FromWeekOffset, int ToWeekOffset)>> buildWindow,
        string sportlinkApiUrl, string sportlinkClientId,
        ILogger log,
        Func<DateOnly>? bepaalVandaag = null)
    {
        var uitkomst = new SyncUitkomst();
        // #1779: één herhaalbudget voor alle dataservice-GET's van deze run (begrenst een brede storing).
        var budget = new HerhaalBudget();
        var clubCode = AppSettings.GetSetting("clubCode");
        if (string.IsNullOrWhiteSpace(clubCode))
            throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings — sync kan niet doorgaan zonder ClubCode.");

        await using var stagingLease = await SqlServerSyncStagingLease.TryAcquireAsync()
            ?? throw new SyncAlreadyRunningException();

        await SqlServerSeasonRollover.EnsureAsync(
            clubCode, bepaalVandaag?.Invoke() ?? SqlServerSeasonRollover.VandaagInNederland, log);
        var (fromWeekOffset, toWeekOffset) = await buildWindow();

        var teamsFailed = uitkomst.Registreer(SyncUitkomst.FaseTeams,
            await FetchTeamsPhaseAsync(sportlinkApiUrl, sportlinkClientId, clubCode, budget, log));
        var programmaFailed = uitkomst.Registreer(SyncUitkomst.FaseProgramma,
            await FetchProgrammaPhaseAsync(fromWeekOffset, toWeekOffset, sportlinkApiUrl, sportlinkClientId, clubCode, budget, log));
        var uitslagenFailed = uitkomst.Registreer(SyncUitkomst.FaseUitslagen,
            await FetchUitslagenPhaseAsync(fromWeekOffset, sportlinkApiUrl, sportlinkClientId, clubCode, budget, log));
        var matchesFailed = programmaFailed || uitslagenFailed;
        // #1547: een gespeelde wedstrijd komt alleen nog via /uitslagen binnen, zonder veld/datum/team.
        await SportlinkStagingRepository.VulUitslagRijenAanUitHisAsync(clubCode, log);
        uitkomst.Registreer(SyncUitkomst.FaseMatchdetails,
            await FetchMatchDetailsPhaseAsync(sportlinkApiUrl, sportlinkClientId, clubCode, budget, log));

        await MergeAllToHisAsync(log);

        // Reconciliatie (#1558, pariteit met #1193): verdwenen teams/wedstrijden zacht verwijderen.
        // Best-effort; draait vóór de canonicalisatie zodat die met opgeschoonde data werkt.
        await SqlServerReconciliation.ReconcileVerdwenenAsync(
            clubCode, teamsFailed, matchesFailed, global::Planner.Shared.Sync.ReconciliatieOndergrens.Nu(), log);

        await RefreshTeamCanonicalisatieAsync(clubCode, log);

        await Planner.PlannerDataAccess.MarkeerVervallenGeplandeWedstrijdenAsync(log);

        if (!uitkomst.GedeeltelijkMislukt)
            await AppSettings.SaveLastSyncTimestampAsync(log);
        else
            log.LogWarning("Sync gedeeltelijk mislukt — LastSyncTimestamp NIET bijgewerkt");

        return uitkomst;
    }

    private static async Task<bool> FetchTeamsPhaseAsync(string sportlinkApiUrl, string sportlinkClientId, string clubCode, HerhaalBudget budget, ILogger log)
    {
        await CreateStagingTable.ExecuteAsync("teams");
        try
        {
            var volledig = await FetchAndStoreTeamsAsync($"{sportlinkApiUrl}/teams?{sportlinkClientId}", clubCode, log, budget: budget);
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

    private static async Task<bool> FetchProgrammaPhaseAsync(int fromWeekOffset, int toWeekOffset, string sportlinkApiUrl, string sportlinkClientId, string clubCode, HerhaalBudget budget, ILogger log)
    {
        await CreateStagingTable.ExecuteAsync("matches");

        log.LogInformation("MATCHES/PROGRAMMA - Fetching weekOffset {From} to {To}", fromWeekOffset, toWeekOffset);
        var partialFailure = false;
        for (int weekOffset = fromWeekOffset; weekOffset <= toWeekOffset; weekOffset++)
        {
            try
            {
                var volledig = await FetchAndStoreProgrammaAsync(
                    $"{sportlinkApiUrl}/programma?{sportlinkClientId}&weekoffset={weekOffset}&aantaldagen={ProgrammaAfkapping.VensterDagen}",
                    clubCode, log, budget: budget);
                log.LogInformation("MATCHES/PROGRAMMA - GET weekOffset={WeekOffset}", weekOffset);
                // #1915: een afgekapt venster is een onvolledige snapshot — nooit reconciliëren op wat ontbreekt.
                if (!volledig) partialFailure = true;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "MATCHES/PROGRAMMA - fetch mislukt weekOffset={WeekOffset}", weekOffset);
                partialFailure = true;
            }
        }
        return partialFailure;
    }

    private static async Task<bool> FetchUitslagenPhaseAsync(int fromWeekOffset, string sportlinkApiUrl, string sportlinkClientId, string clubCode, HerhaalBudget budget, ILogger log)
    {
        int scoreFrom = Math.Min(fromWeekOffset, -2);
        log.LogInformation("MATCHES/UITSLAGEN - Fetching weekOffset {From} to 0", scoreFrom);
        var partialFailure = false;
        for (int weekOffset = scoreFrom; weekOffset <= 0; weekOffset++)
        {
            try
            {
                await FetchAndStoreUitslagenAsync(
                    $"{sportlinkApiUrl}/uitslagen?{sportlinkClientId}&weekoffset={weekOffset}", clubCode, log, budget: budget);
                log.LogInformation("MATCHES/UITSLAGEN - GET weekOffset={WeekOffset}", weekOffset);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "MATCHES/UITSLAGEN - fetch mislukt weekOffset={WeekOffset}", weekOffset);
                partialFailure = true;
            }
        }
        return partialFailure;
    }

    private static async Task<bool> FetchMatchDetailsPhaseAsync(string sportlinkApiUrl, string sportlinkClientId, string clubCode, HerhaalBudget budget, ILogger log)
    {
        await CreateStagingTable.ExecuteAsync("matchdetails");
        var wedstrijdcodes = await SportlinkStagingRepository.GetWedstrijdcodesAsync();
        int mdOk = 0, mdFout = 0;
        foreach (var wedstrijdcode in wedstrijdcodes)
        {
            if (await FetchAndStoreMatchDetailsAsync(
                    $"{sportlinkApiUrl}/wedstrijd-informatie?{sportlinkClientId}&wedstrijdcode={wedstrijdcode}",
                    clubCode, log, budget: budget))
            {
                mdOk++;
                log.LogInformation("MATCHDETAILS - GET wedstrijdcode={Code}", wedstrijdcode);
            }
            else
            {
                mdFout++;
                // De foutregel in FetchAndStoreMatchDetailsAsync noemt bewust geen URL meer (#1200);
                // de wedstrijdcode is hier wél veilig te loggen en houdt de fout herleidbaar.
                log.LogWarning("MATCHDETAILS - mislukt voor wedstrijdcode={Code}", wedstrijdcode);
            }
        }
        log.LogInformation("MATCHDETAILS - {Ok} succesvol, {Fout} mislukt van {Total}",
            mdOk, mdFout, wedstrijdcodes.Count);
        return mdFout > 0;
    }

    private static async Task MergeAllToHisAsync(ILogger log)
    {
        await new MergeStgToHis("stg", "teams",        "his", "teams").ExecuteAsync(log);
        await new MergeStgToHis("stg", "matches",      "his", "matches").ExecuteAsync(log);
        await new MergeStgToHis("stg", "matchdetails", "his", "matchdetails").ExecuteAsync(log);
    }

    private static async Task RefreshTeamCanonicalisatieAsync(string clubCode, ILogger log)
    {
        try
        {
            await TeamResolution.TeamCanonicalisatieService.RefreshAsync(clubCode, log);
        }
        catch (Exception ex)
        {
            // Nooit de hele sync laten falen op de teamcanonicalisatie (#696) — his.teams/matches
            // zijn al gemerged; de bestaande regex/LIKE-matching blijft intussen het vangnet.
            log.LogError(ex, "TEAMS CANONICALISATIE - mislukt voor club {ClubCode}", clubCode);
        }

        // AllStars FC (#756) heeft geen eigen Sportlink-sync — zijn his.teams-rijen komen uit de
        // PostDeployment-demodata-seed, niet uit deze pipeline. Zonder deze aanroep blijft dbo.Teams
        // voor de democlub voor altijd leeg, terwijl her/matches wel gevuld zijn: de teamdropdown in de
        // Admin UI zou dan voor de democlub 0 teams tonen in plaats van de rauwe (niet-ontdubbelde) lijst
        // van vóór #756. Meelopen op elke echte sync houdt de demodata dus canoniek zonder een aparte job.
        if (!clubCode.Equals(AllstarsClubCode, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await TeamResolution.TeamCanonicalisatieService.RefreshAsync(AllstarsClubCode, log);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "TEAMS CANONICALISATIE - mislukt voor democlub ALLSTARS");
            }
        }
    }

    // #1779: alle dataservice-GET's lopen via SportlinkDataserviceHttp (begrensde herhaling op
    // 5xx/429/time-out, runbreed budget); zonder budget (tests) geldt alleen de grens per aanroep.
    internal static async Task<bool> FetchAndStoreTeamsAsync(string apiUrl, string clubCode, ILogger log,
        HttpClient? httpClient = null, TimeSpan? herhaalWachttijd = null, HerhaalBudget? budget = null)
    {
        var json  = await SportlinkDataserviceHttp.HaalTekstAsync(
            httpClient ?? _client, apiUrl, budget ?? new HerhaalBudget(), "TEAMS", log, herhaalWachttijd);
        var teams = JsonConvert.DeserializeObject<List<Team>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (teams is { Count: > 0 })
        {
            log.LogInformation("TEAMS - {Count} gevonden.", teams.Count);
            await SportlinkStagingRepository.SaveTeamsAsync(teams, clubCode, log);
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
    internal static async Task<bool> FetchAndStoreProgrammaAsync(string apiUrl, string clubCode, ILogger log,
        HttpClient? httpClient = null, TimeSpan? herhaalWachttijd = null, HerhaalBudget? budget = null)
    {
        var herhaalBudget = budget ?? new HerhaalBudget();
        var ophaling = await ProgrammaAfkapping.HaalVolledigAsync<Match>(apiUrl, async url =>
        {
            var json = await SportlinkDataserviceHttp.HaalTekstAsync(
                httpClient ?? _client, url, herhaalBudget, "MATCHES/PROGRAMMA", log, herhaalWachttijd);
            return JsonConvert.DeserializeObject<List<Match>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore })
                ?? new List<Match>();
        }, () => SportlinkStagingRepository.GetTeamcodesAsync());
        if (ophaling.Gesplitst)
            log.LogInformation("MATCHES/PROGRAMMA - venster raakte het plafond van {Plafond} rijen; opgehaald {Wijze} ({Count} rijen).",
                ProgrammaAfkapping.Plafond, ophaling.PerTeam ? "per team" : "als thuis + uit", ophaling.Rijen.Count);
        if (!ophaling.Volledig)
            log.LogWarning("MATCHES/PROGRAMMA - venster is ook na splitsing (thuis/uit, per team) afgekapt op {Plafond} rijen; sync telt als onvolledig.",
                ProgrammaAfkapping.Plafond);
        if (ophaling.Rijen.Count > 0)
        {
            log.LogInformation("MATCHES/PROGRAMMA - {Count} gevonden.", ophaling.Rijen.Count);
            await SportlinkStagingRepository.SaveProgrammaAsync(ophaling.Rijen.ToList(), clubCode, log);
        }
        return ophaling.Volledig;
    }

    internal static async Task FetchAndStoreUitslagenAsync(string apiUrl, string clubCode, ILogger log,
        HttpClient? httpClient = null, TimeSpan? herhaalWachttijd = null, HerhaalBudget? budget = null)
    {
        var json    = await SportlinkDataserviceHttp.HaalTekstAsync(
            httpClient ?? _client, apiUrl, budget ?? new HerhaalBudget(), "MATCHES/UITSLAGEN", log, herhaalWachttijd);
        var matches = JsonConvert.DeserializeObject<List<Match>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (matches is { Count: > 0 })
        {
            log.LogInformation("MATCHES/UITSLAGEN - {Count} gevonden.", matches.Count);
            await SportlinkStagingRepository.MergeUitslagenAsync(matches, clubCode, log);
        }
    }

    // Retourneert true bij succes, false bij elke fout — zodat de caller partialFailure kan bijhouden. (#464)
    // httpClient is optioneel; standaard wordt de static _client gebruikt. (#476 — testbaar via inject)
    internal static async Task<bool> FetchAndStoreMatchDetailsAsync(string apiUrl, string clubCode, ILogger log,
        HttpClient? httpClient = null, TimeSpan? herhaalWachttijd = null, HerhaalBudget? budget = null)
    {
        try
        {
            // #1779: begrensde herhaling op 5xx/429/time-out (idempotente GET); logt nooit de URL.
            var json = await SportlinkDataserviceHttp.HaalTekstAsync(
                httpClient ?? _client, apiUrl, budget ?? new HerhaalBudget(), "MATCHDETAILS", log, herhaalWachttijd);
            try
            {
                var details = JsonConvert.DeserializeObject<MatchDetails>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                if (details != null)
                    await SportlinkStagingRepository.SaveMatchDetailsAsync(details, clubCode, log);
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
