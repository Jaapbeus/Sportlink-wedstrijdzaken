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

    // partialFailure: als één stap faalt, slaan we LastSyncTimestamp NIET op. (#438, #464)
    internal static async Task RunSyncAsync(
        int fromWeekOffset, int toWeekOffset,
        string sportlinkApiUrl, string sportlinkClientId,
        ILogger log)
    {
        var partialFailure = false;

        await CreateStagingTable.ExecuteAsync("teams");
        try
        {
            await FetchAndStoreTeamsAsync($"{sportlinkApiUrl}/teams?{sportlinkClientId}", log);
            log.LogInformation("TEAMS - GET endpoint=/teams");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TEAMS - fetch mislukt");
            partialFailure = true;
        }

        await CreateStagingTable.ExecuteAsync("matches");

        log.LogInformation("MATCHES/PROGRAMMA - Fetching weekOffset {From} to {To}", fromWeekOffset, toWeekOffset);
        for (int weekOffset = fromWeekOffset; weekOffset <= toWeekOffset; weekOffset++)
        {
            try
            {
                await FetchAndStoreProgrammaAsync(
                    $"{sportlinkApiUrl}/programma?{sportlinkClientId}&weekoffset={weekOffset}", log);
                log.LogInformation("MATCHES/PROGRAMMA - GET weekOffset={WeekOffset}", weekOffset);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "MATCHES/PROGRAMMA - fetch mislukt weekOffset={WeekOffset}", weekOffset);
                partialFailure = true;
            }
        }

        int scoreFrom = Math.Min(fromWeekOffset, -2);
        log.LogInformation("MATCHES/UITSLAGEN - Fetching weekOffset {From} to 0", scoreFrom);
        for (int weekOffset = scoreFrom; weekOffset <= 0; weekOffset++)
        {
            try
            {
                await FetchAndStoreUitslagenAsync(
                    $"{sportlinkApiUrl}/uitslagen?{sportlinkClientId}&weekoffset={weekOffset}", log);
                log.LogInformation("MATCHES/UITSLAGEN - GET weekOffset={WeekOffset}", weekOffset);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "MATCHES/UITSLAGEN - fetch mislukt weekOffset={WeekOffset}", weekOffset);
                partialFailure = true;
            }
        }

        await CreateStagingTable.ExecuteAsync("matchdetails");
        var wedstrijdcodes = await SportlinkStagingRepository.GetWedstrijdcodesAsync(log);
        int mdOk = 0, mdFout = 0;
        foreach (var wedstrijdcode in wedstrijdcodes)
        {
            if (await FetchAndStoreMatchDetailsAsync(
                    $"{sportlinkApiUrl}/wedstrijd-informatie?{sportlinkClientId}&wedstrijdcode={wedstrijdcode}",
                    log))
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

        await new MergeStgToHis("stg", "teams",        "his", "teams").ExecuteAsync(log);
        await new MergeStgToHis("stg", "matches",      "his", "matches").ExecuteAsync(log);
        await new MergeStgToHis("stg", "matchdetails", "his", "matchdetails").ExecuteAsync(log);

        await Planner.PlannerDataAccess.MarkeerVervallenGeplandeWedstrijdenAsync(log);

        if (!partialFailure)
            await AppSettings.SaveLastSyncTimestampAsync(log);
        else
            log.LogWarning("Sync gedeeltelijk mislukt — LastSyncTimestamp NIET bijgewerkt");
    }

    private static async Task FetchAndStoreTeamsAsync(string apiUrl, ILogger log)
    {
        var response = await _client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        var json  = await response.Content.ReadAsStringAsync();
        var teams = JsonConvert.DeserializeObject<List<Team>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (teams != null)
        {
            log.LogInformation("TEAMS - {Count} gevonden.", teams.Count);
            await SportlinkStagingRepository.SaveTeamsAsync(teams, log);
        }
        else
        {
            log.LogWarning("TEAMS - geen data gevonden.");
        }
    }

    private static async Task FetchAndStoreProgrammaAsync(string apiUrl, ILogger log)
    {
        var response = await _client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        var json    = await response.Content.ReadAsStringAsync();
        var matches = JsonConvert.DeserializeObject<List<Match>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (matches is { Count: > 0 })
        {
            log.LogInformation("MATCHES/PROGRAMMA - {Count} gevonden.", matches.Count);
            await SportlinkStagingRepository.SaveProgrammaAsync(matches, log);
        }
    }

    private static async Task FetchAndStoreUitslagenAsync(string apiUrl, ILogger log)
    {
        var response = await _client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        var json    = await response.Content.ReadAsStringAsync();
        var matches = JsonConvert.DeserializeObject<List<Match>>(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        if (matches is { Count: > 0 })
        {
            log.LogInformation("MATCHES/UITSLAGEN - {Count} gevonden.", matches.Count);
            await SportlinkStagingRepository.MergeUitslagenAsync(matches, log);
        }
    }

    // Retourneert true bij succes, false bij elke fout — zodat de caller partialFailure kan bijhouden. (#464)
    // httpClient is optioneel; standaard wordt de static _client gebruikt. (#476 — testbaar via inject)
    internal static async Task<bool> FetchAndStoreMatchDetailsAsync(string apiUrl, ILogger log, HttpClient? httpClient = null)
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
                    await SportlinkStagingRepository.SaveMatchDetailsAsync(details, log);
                else
                    log.LogWarning("MATCHDETAILS - geen data gevonden.");
            }
            catch (JsonSerializationException ex)
            {
                log.LogError("MATCHDETAILS - JSON-deserialisatiefout: {Message}", ex.Message);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.LogError("MATCHDETAILS - ophalen mislukt voor {Url}: {Message}", apiUrl, ex.Message);
            return false;
        }
    }
}
