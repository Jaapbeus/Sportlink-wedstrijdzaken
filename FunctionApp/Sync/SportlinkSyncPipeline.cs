using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using static SportlinkFunction.SystemUtilities;

namespace SportlinkFunction;

/// <summary>
/// Orkestreert de volledige Sportlink-sync: API ophalen → staging → merge → timestamp.
/// Extracted uit Function1.cs (#466).
/// </summary>
internal static class SportlinkSyncPipeline
{
    private static readonly HttpClient _client = new();
    private const string AllstarsClubCode = "ALLSTARS";

    // partialFailure: als één stap faalt, slaan we LastSyncTimestamp NIET op. (#438, #464)
    internal static async Task RunSyncAsync(
        int fromWeekOffset, int toWeekOffset,
        string sportlinkApiUrl, string sportlinkClientId,
        ILogger log)
    {
        var partialFailure = false;
        var clubCode = AppSettings.GetSetting("clubCode");
        if (string.IsNullOrWhiteSpace(clubCode))
            throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings — sync kan niet doorgaan zonder ClubCode.");

        partialFailure |= await FetchTeamsPhaseAsync(sportlinkApiUrl, sportlinkClientId, clubCode, log);
        partialFailure |= await FetchProgrammaPhaseAsync(fromWeekOffset, toWeekOffset, sportlinkApiUrl, sportlinkClientId, clubCode, log);
        partialFailure |= await FetchUitslagenPhaseAsync(fromWeekOffset, sportlinkApiUrl, sportlinkClientId, clubCode, log);
        partialFailure |= await FetchMatchDetailsPhaseAsync(sportlinkApiUrl, sportlinkClientId, clubCode, log);

        await MergeAllToHisAsync(log);
        await RefreshTeamCanonicalisatieAsync(clubCode, log);

        await Planner.PlannerDataAccess.MarkeerVervallenGeplandeWedstrijdenAsync(log);

        if (!partialFailure)
            await AppSettings.SaveLastSyncTimestampAsync(log);
        else
            log.LogWarning("Sync gedeeltelijk mislukt — LastSyncTimestamp NIET bijgewerkt");
    }

    private static async Task<bool> FetchTeamsPhaseAsync(string sportlinkApiUrl, string sportlinkClientId, string clubCode, ILogger log)
    {
        await CreateStagingTable.ExecuteAsync("teams");
        try
        {
            await FetchAndStoreTeamsAsync($"{sportlinkApiUrl}/teams?{sportlinkClientId}", clubCode, log);
            log.LogInformation("TEAMS - GET endpoint=/teams");
            return false;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TEAMS - fetch mislukt");
            return true;
        }
    }

    private static async Task<bool> FetchProgrammaPhaseAsync(int fromWeekOffset, int toWeekOffset, string sportlinkApiUrl, string sportlinkClientId, string clubCode, ILogger log)
    {
        await CreateStagingTable.ExecuteAsync("matches");

        log.LogInformation("MATCHES/PROGRAMMA - Fetching weekOffset {From} to {To}", fromWeekOffset, toWeekOffset);
        var partialFailure = false;
        for (int weekOffset = fromWeekOffset; weekOffset <= toWeekOffset; weekOffset++)
        {
            try
            {
                await FetchAndStoreProgrammaAsync(
                    $"{sportlinkApiUrl}/programma?{sportlinkClientId}&weekoffset={weekOffset}", clubCode, log);
                log.LogInformation("MATCHES/PROGRAMMA - GET weekOffset={WeekOffset}", weekOffset);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "MATCHES/PROGRAMMA - fetch mislukt weekOffset={WeekOffset}", weekOffset);
                partialFailure = true;
            }
        }
        return partialFailure;
    }

    private static async Task<bool> FetchUitslagenPhaseAsync(int fromWeekOffset, string sportlinkApiUrl, string sportlinkClientId, string clubCode, ILogger log)
    {
        int scoreFrom = Math.Min(fromWeekOffset, -2);
        log.LogInformation("MATCHES/UITSLAGEN - Fetching weekOffset {From} to 0", scoreFrom);
        var partialFailure = false;
        for (int weekOffset = scoreFrom; weekOffset <= 0; weekOffset++)
        {
            try
            {
                await FetchAndStoreUitslagenAsync(
                    $"{sportlinkApiUrl}/uitslagen?{sportlinkClientId}&weekoffset={weekOffset}", clubCode, log);
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

    private static async Task<bool> FetchMatchDetailsPhaseAsync(string sportlinkApiUrl, string sportlinkClientId, string clubCode, ILogger log)
    {
        await CreateStagingTable.ExecuteAsync("matchdetails");
        var wedstrijdcodes = await SportlinkStagingRepository.GetWedstrijdcodesAsync();
        int mdOk = 0, mdFout = 0;
        foreach (var wedstrijdcode in wedstrijdcodes)
        {
            if (await FetchAndStoreMatchDetailsAsync(
                    $"{sportlinkApiUrl}/wedstrijd-informatie?{sportlinkClientId}&wedstrijdcode={wedstrijdcode}",
                    clubCode, log))
            {
                mdOk++;
                log.LogInformation("MATCHDETAILS - GET wedstrijdcode={Code}", wedstrijdcode);
            }
            else
            {
                mdFout++;
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

    private static async Task FetchAndStoreTeamsAsync(string apiUrl, string clubCode, ILogger log)
    {
        var response = await _client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        var json  = await response.Content.ReadAsStringAsync();
        var teams = JsonConvert.DeserializeObject<List<Team>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (teams != null)
        {
            log.LogInformation("TEAMS - {Count} gevonden.", teams.Count);
            await SportlinkStagingRepository.SaveTeamsAsync(teams, clubCode, log);
        }
        else
        {
            log.LogWarning("TEAMS - geen data gevonden.");
        }
    }

    private static async Task FetchAndStoreProgrammaAsync(string apiUrl, string clubCode, ILogger log)
    {
        var response = await _client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        var json    = await response.Content.ReadAsStringAsync();
        var matches = JsonConvert.DeserializeObject<List<Match>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (matches is { Count: > 0 })
        {
            log.LogInformation("MATCHES/PROGRAMMA - {Count} gevonden.", matches.Count);
            await SportlinkStagingRepository.SaveProgrammaAsync(matches, clubCode, log);
        }
    }

    private static async Task FetchAndStoreUitslagenAsync(string apiUrl, string clubCode, ILogger log)
    {
        var response = await _client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        var json    = await response.Content.ReadAsStringAsync();
        var matches = JsonConvert.DeserializeObject<List<Match>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (matches is { Count: > 0 })
        {
            log.LogInformation("MATCHES/UITSLAGEN - {Count} gevonden.", matches.Count);
            await SportlinkStagingRepository.MergeUitslagenAsync(matches, clubCode, log);
        }
    }

    // Retourneert true bij succes, false bij elke fout — zodat de caller partialFailure kan bijhouden. (#464)
    // httpClient is optioneel; standaard wordt de static _client gebruikt. (#476 — testbaar via inject)
    internal static async Task<bool> FetchAndStoreMatchDetailsAsync(string apiUrl, string clubCode, ILogger log, HttpClient? httpClient = null)
    {
        var client = httpClient ?? _client;
        try
        {
            var response = await client.GetAsync(apiUrl);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
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
            log.LogError(ex, "MATCHDETAILS - ophalen mislukt voor {Url}", apiUrl);
            return false;
        }
    }
}
