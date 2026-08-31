using Database.Postgres;
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
/// <b>Bewust NIET meegenomen — twee gedocumenteerde afwijkingen van het origineel:</b>
/// </para>
/// <list type="bullet">
/// <item><b>Teamcanonicalisatie</b> (<c>TeamResolution.TeamCanonicalisatieService.RefreshAsync</c>)
/// is in het origineel al best-effort (try/catch, mag falen zonder de sync te breken) — op de
/// Postgres-tier bestaat deze service nog niet, dus wordt de aanroep overgeslagen in plaats van
/// een nooit-geslaagde try/catch te faken. <c>his.teams</c>/<c>his.matches</c> worden hierdoor wel
/// gevuld; alleen de afgeleide, ontdubbelde <c>dbo.Teams</c>-achtige canonicalisatie ontbreekt nog.</item>
/// <item><b><c>MarkeerVervallenGeplandeWedstrijdenAsync</c></b> is in het origineel ONGUARD (geen
/// try/catch) — een falen daar hoort de hele sync te laten falen. Omdat deze logica nog niet
/// bestaat op de Postgres-tier, is dit een echt, tijdelijk gat (geen gelijkwaardig gedrag) in
/// plaats van een neptoevoeging. Zie issue 890 vervolgwerk.</item>
/// </list>
/// <para>
/// <b>Seizoensgrenzen (<c>dbo.Season</c>) zijn hier evenmin geport</b> — <see cref="RunSyncAsync"/>
/// zelf heeft daar geen afhankelijkheid van (het neemt <c>fromWeekOffset</c>/<c>toWeekOffset</c> als
/// expliciete parameters, exact zoals het origineel), maar de buitenste triggers
/// (<see cref="SyncFunction"/>) kunnen de seizoenseinde-datum nog niet opzoeken — zie de
/// documentatie daar.
/// </para>
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

        // Teamcanonicalisatie en MarkeerVervallenGeplandeWedstrijden: zie klasse-doc-comment
        // hierboven — bewust (nog) niet geport, geen equivalent gedrag.

        if (!partialFailure)
            await SaveLastSyncTimestampAsync(connectionString, clubCode, log);
        else
            log.LogWarning("Sync gedeeltelijk mislukt — lastsynctimestamp NIET bijgewerkt");
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
