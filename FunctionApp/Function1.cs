using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using static SportlinkFunction.SystemUtilities;

namespace SportlinkFunction
{
    public static class FetchAndStoreApiData
    {
        private static readonly HttpClient client = new HttpClient();

        [Function("FetchAndStoreApiData")]
        public static async Task Run([TimerTrigger("0 0 4 * * *")] TimerInfo myTimer, FunctionContext context)
        {
            var log = context.GetLogger("FetchAndStoreApiData");
            log.LogInformation($"Azure Function executed at: {DateTime.Now}");

            try
            {
                await SystemUtilities.WaitForDatabaseAsync(log);
                await SystemUtilities.AppSettings.LoadSettingsAsync(log);

                string? sportlinkApiUrl = SystemUtilities.AppSettings.GetSetting("sportlinkApiUrl");
                if (string.IsNullOrEmpty(sportlinkApiUrl))
                {
                    log.LogError("sportlinkApiUrl is not configured.");
                    return;
                }
                string sportlinkClientId = $"clientId={SystemUtilities.AppSettings.GetSetting("sportlinkClientId")}";

                // Default: previous week (-1) up to end of current season
                int toWeekOffset = await SeasonHelper.GetSeasonEndWeekOffsetAsync(log);
                log.LogInformation($"Sync range: weekOffset -1 to {toWeekOffset} (end of season)");
                await RunSyncAsync(-1, toWeekOffset, sportlinkApiUrl, sportlinkClientId, log);
            }
            catch (Exception ex)
            {
                log.LogError($"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// HTTP trigger to manually start a sync.
        /// Default (no params): same as timer — previous week through end of season.
        /// Reset mode: GET /api/sync-matches?reset=true&amp;season=2024
        ///   Downloads all matches from the start of the given season year through end of current season.
        /// </summary>
        [Function("SyncMatchesHttp")]
        public static async Task<IActionResult> SyncMatchesHttp(
            [HttpTrigger(AuthorizationLevel.Admin, "get", Route = "sync-matches")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("SyncMatchesHttp");
            log.LogInformation($"HTTP trigger SyncMatchesHttp executed at: {DateTime.Now}");

            try
            {
                await SystemUtilities.WaitForDatabaseAsync(log);
                await SystemUtilities.AppSettings.LoadSettingsAsync(log);

                string? sportlinkApiUrl = SystemUtilities.AppSettings.GetSetting("sportlinkApiUrl");
                if (string.IsNullOrEmpty(sportlinkApiUrl))
                {
                    log.LogError("sportlinkApiUrl is not configured.");
                    return new StatusCodeResult(500);
                }
                string sportlinkClientId = $"clientId={SystemUtilities.AppSettings.GetSetting("sportlinkClientId")}";

                bool isReset = string.Equals(req.Query["reset"], "true", StringComparison.OrdinalIgnoreCase);
                string? seasonParam = req.Query["season"];

                int toWeekOffset = await SeasonHelper.GetSeasonEndWeekOffsetAsync(log);
                int fromWeekOffset = -1; // default: previous week

                if (isReset && int.TryParse(seasonParam, out int seasonStartYear))
                {
                    fromWeekOffset = await SeasonHelper.GetSeasonStartWeekOffsetAsync(seasonStartYear, log);
                    log.LogInformation($"Reset mode: season {seasonStartYear}, weekOffset {fromWeekOffset} to {toWeekOffset}");
                }
                else
                {
                    log.LogInformation($"Default mode: weekOffset {fromWeekOffset} to {toWeekOffset}");
                }

                await RunSyncAsync(fromWeekOffset, toWeekOffset, sportlinkApiUrl, sportlinkClientId, log);
                return new OkObjectResult($"Sync completed. WeekOffset range: {fromWeekOffset} to {toWeekOffset}.");
            }
            catch (Exception ex)
            {
                log.LogError($"Error: {ex.Message}");
                return new StatusCodeResult(500);
            }
        }

        private static async Task RunSyncAsync(int fromWeekOffset, int toWeekOffset, string sportlinkApiUrl, string sportlinkClientId, ILogger log)
        {
            string ApiUrl;

            // Drop and create staging table
            await CreateStagingTable.ExecuteAsync("teams");
            // Fetch and store Teams data
            ApiUrl = $"{sportlinkApiUrl}/teams?{sportlinkClientId}";
            await FetchAndStoreTeamsData(ApiUrl, log);
            log.LogInformation($"TEAMS - GET: {ApiUrl}");

            // Drop and create staging table
            await CreateStagingTable.ExecuteAsync("matches");

            // Step 1: /programma — fetch all weeks (past + future) for planning info
            // (referee, venue, kleedkamers, logos, gather/depart times)
            log.LogInformation($"MATCHES/PROGRAMMA - Fetching weekOffset {fromWeekOffset} to {toWeekOffset}");
            for (int weekOffset = fromWeekOffset; weekOffset <= toWeekOffset; weekOffset++)
            {
                ApiUrl = $"{sportlinkApiUrl}/programma?{sportlinkClientId}&weekoffset={weekOffset}";
                await FetchAndStoreProgrammaMatches(ApiUrl, log);
                log.LogInformation($"MATCHES/PROGRAMMA - GET {ApiUrl}");
            }

            // Step 2: /uitslagen — fetch past weeks only for scores (uitslag, uitslag-regulier, etc.)
            // Updates rows already inserted by /programma; inserts historical rows not in /programma.
            int scoreFromOffset = Math.Min(fromWeekOffset, -2);
            log.LogInformation($"MATCHES/UITSLAGEN - Fetching scores weekOffset {scoreFromOffset} to 0");
            for (int weekOffset = scoreFromOffset; weekOffset <= 0; weekOffset++)
            {
                ApiUrl = $"{sportlinkApiUrl}/uitslagen?{sportlinkClientId}&weekoffset={weekOffset}";
                await FetchAndStoreUitslagenScores(ApiUrl, log);
                log.LogInformation($"MATCHES/UITSLAGEN - GET {ApiUrl}");
            }
            // Drop and create staging table
            await CreateStagingTable.ExecuteAsync("matchdetails");
            // Fetch and store match details for each match in the staging table
            var wedstrijdcodes = await FetchWedstrijdcodesFromStagingMatches(log);
            foreach (var wedstrijdcode in wedstrijdcodes)
            {
                ApiUrl = $"{sportlinkApiUrl}/wedstrijd-informatie?{sportlinkClientId}&wedstrijdcode={wedstrijdcode}";
                await FetchAndStoreMatchDetails(ApiUrl, log);
                log.LogInformation($"MATCHDETAILS - GET: {ApiUrl}");
            }
            // Merge staging data into history tables
            await new MergeStgToHis("stg", "teams",        "his", "teams").ExecuteAsync(log);
            await new MergeStgToHis("stg", "matches",      "his", "matches").ExecuteAsync(log);
            await new MergeStgToHis("stg", "matchdetails", "his", "matchdetails").ExecuteAsync(log);
        }

        private static async Task FetchAndStoreMatchDetails(string apiUrl, ILogger log)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    var response = await client.GetAsync(apiUrl);
                    response.EnsureSuccessStatusCode();

                    string jsonResponse = await response.Content.ReadAsStringAsync();
                    try
                    {
                        MatchDetails? matchDetails = JsonConvert.DeserializeObject<MatchDetails>(jsonResponse, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                        if (matchDetails != null)
                        {
                            await SaveToDatabaseMatchDetails(matchDetails, log);
                        }
                        else
                        {
                            log.LogWarning("MATCHDETAILS - No match details found.");
                        }
                    }
                    catch (JsonSerializationException ex)
                    {
                        log.LogError($"Error deserializing JSON: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError($"Error fetching match details from {apiUrl}: {ex.Message}");
            }
        }
        private static async Task<List<string>> FetchWedstrijdcodesFromStagingMatches(ILogger log)
        {
            try
            {
                var query = "SELECT wedstrijdcode FROM [stg].[matches]";
                var wedstrijdcodes = new List<string>();
                using (var connection = new Microsoft.Data.SqlClient.SqlConnection(DatabaseConfig.ConnectionString))
                {
                    await connection.OpenAsync();
                    using (var command = new Microsoft.Data.SqlClient.SqlCommand(query, connection))
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var wedstrijdcode = reader["wedstrijdcode"]?.ToString();
                            if (wedstrijdcode != null)
                            {
                                wedstrijdcodes.Add(wedstrijdcode);
                            }
                        }
                    }
                    connection.Close();
                }
                return wedstrijdcodes;
            }
            catch (Exception ex)
            {
                log.LogError($"Error fetching wedstrijdcodes from database: {ex.Message}");
                throw;
            }
        }


        private static async Task FetchAndStoreTeamsData(string apiUrl, ILogger log)
        {
            try
            {
                HttpResponseMessage response = await client.GetAsync(apiUrl);
                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync();
                List<Team>? teams = JsonConvert.DeserializeObject<List<Team>>(jsonResponse, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                if (teams != null)
                {
                    log.LogInformation($"TEAMS - {teams.Count} count.");
                    await SaveToDatabaseTeams(teams, log);
                }
                else
                {
                    log.LogWarning("TEAMS - No teams data found.");
                }
            }
            catch (Exception ex)
            {
                log.LogError($"TEAMS - Error fetching or deserializing: {ex.Message}");
            }
        }

        private static async Task FetchAndStoreProgrammaMatches(string apiUrl, ILogger log)
        {
            try
            {
                HttpResponseMessage response = await client.GetAsync(apiUrl);
                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync();
                List<Match>? matches = JsonConvert.DeserializeObject<List<Match>>(jsonResponse, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                if (matches != null && matches.Count > 0)
                {
                    log.LogInformation($"MATCHES/PROGRAMMA - {matches.Count} count.");
                    await SaveProgrammaToStaging(matches, log);
                }
            }
            catch (Exception ex)
            {
                log.LogError($"MATCHES/PROGRAMMA - Error fetching or deserializing: {ex.Message}");
            }
        }

        private static async Task FetchAndStoreUitslagenScores(string apiUrl, ILogger log)
        {
            try
            {
                HttpResponseMessage response = await client.GetAsync(apiUrl);
                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync();
                List<Match>? matches = JsonConvert.DeserializeObject<List<Match>>(jsonResponse, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                if (matches != null && matches.Count > 0)
                {
                    log.LogInformation($"MATCHES/UITSLAGEN - {matches.Count} count.");
                    await MergeUitslagenIntoStaging(matches, log);
                }
            }
            catch (Exception ex)
            {
                log.LogError($"MATCHES/UITSLAGEN - Error fetching or deserializing: {ex.Message}");
            }
        }

        private static async Task SaveToDatabaseMatchDetails(MatchDetails matchDetails, ILogger log)
        {
            using (SqlConnection connection = new SqlConnection(DatabaseConfig.ConnectionString))
            {
                await connection.OpenAsync();
                try
                {
                    var query = @"
                    IF NOT EXISTS (SELECT 1 FROM [stg].[matchdetails] WHERE WedstrijdCode = @WedstrijdCode)
                    INSERT INTO [stg].[matchdetails] (
                        WedstrijdCode, InternCode, VeldNaam, VeldLocatie, VertrekTijd, Rijder, 
                        ThuisScore, ThuisScoreRegulier, ThuisScoreNV, ThuisScoreS, UitScore, UitScoreRegulier, 
                        UitScoreNV, UitScoreS, Klasse, WedstrijdType, CompetitieType, Categorie, MatchDateTime, 
                        MatchDate, Aanvangstijd, Duration, SpelType, Aanduiding, PouleCode, Poule, ThuisTeamID, 
                        ThuisTeam, UitTeamID, UitTeam, Opmerkingen, VerenigingScheidsrechterCode, VerenigingScheidsrechter, 
                        OverigeOfficialCode, OverigeOfficial, Scheidsrechters, KleedkamerThuis, KleedkamerUit, KleedkamerOfficial, 
                        AccommodatieNaam, AccommodatieStraat, AccommodatiePlaats, AccommodatieTelefoon, AccommodatieRouteplanner, 
                        ThuisTeamNaam, ThuisTeamCode, ThuisTeamWebsite, ThuisTeamShirtKleur, ThuisTeamStraat, 
                        ThuisTeamPostcodePlaats, ThuisTeamTelefoon, ThuisTeamEmail, UitTeamNaam, UitTeamCode, 
                        UitTeamWebsite, UitTeamShirtKleur, UitTeamStraat, UitTeamPostcodePlaats, UitTeamTelefoon, UitTeamEmail
                    ) 
                    VALUES (
                        @WedstrijdCode, @InternCode, @VeldNaam, @VeldLocatie, @VertrekTijd, @Rijder, 
                        @ThuisScore, @ThuisScoreRegulier, @ThuisScoreNV, @ThuisScoreS, @UitScore, @UitScoreRegulier, 
                        @UitScoreNV, @UitScoreS, @Klasse, @WedstrijdType, @CompetitieType, @Categorie, @MatchDateTime, 
                        @MatchDate, @Aanvangstijd, @Duration, @SpelType, @Aanduiding, @PouleCode, @Poule, @ThuisTeamID, 
                        @ThuisTeam, @UitTeamID, @UitTeam, @Opmerkingen, @VerenigingScheidsrechterCode, @VerenigingScheidsrechter, 
                        @OverigeOfficialCode, @OverigeOfficial, @Scheidsrechters, @KleedkamerThuis, @KleedkamerUit, @KleedkamerOfficial, 
                        @AccommodatieNaam, @AccommodatieStraat, @AccommodatiePlaats, @AccommodatieTelefoon, @AccommodatieRouteplanner, 
                        @ThuisTeamNaam, @ThuisTeamCode, @ThuisTeamWebsite, @ThuisTeamShirtKleur, @ThuisTeamStraat, 
                        @ThuisTeamPostcodePlaats, @ThuisTeamTelefoon, @ThuisTeamEmail, @UitTeamNaam, @UitTeamCode, 
                        @UitTeamWebsite, @UitTeamShirtKleur, @UitTeamStraat, @UitTeamPostcodePlaats, @UitTeamTelefoon, @UitTeamEmail
                    )";

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        // Match wedstrijdinformatie fields
                        command.Parameters.AddWithValue("@WedstrijdCode", matchDetails.Wedstrijdinformatie.Wedstrijdnummer);
                        command.Parameters.AddWithValue("@InternCode", matchDetails.Wedstrijdinformatie.Wedstijdnummerintern);
                        command.Parameters.AddWithValue("@VeldNaam", matchDetails.Wedstrijdinformatie.Veldnaam ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@VeldLocatie", matchDetails.Wedstrijdinformatie.Veldlocatie ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@VertrekTijd", matchDetails.Wedstrijdinformatie.Vertrektijd ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@Rijder", matchDetails.Wedstrijdinformatie.Rijder ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ThuisScore", matchDetails.Wedstrijdinformatie.Thuisscore ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ThuisScoreRegulier", matchDetails.Wedstrijdinformatie.ThuisscoreRegulier ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ThuisScoreNV", matchDetails.Wedstrijdinformatie.ThuisscoreNv ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ThuisScoreS", matchDetails.Wedstrijdinformatie.ThuisscoreS ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitScore", matchDetails.Wedstrijdinformatie.Uitscore ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitScoreRegulier", matchDetails.Wedstrijdinformatie.UitscoreRegulier ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitScoreNV", matchDetails.Wedstrijdinformatie.UitscoreNv ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitScoreS", matchDetails.Wedstrijdinformatie.UitscoreS ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@Klasse", matchDetails.Wedstrijdinformatie.Klasse ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@WedstrijdType", matchDetails.Wedstrijdinformatie.Wedstrijdtype ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@CompetitieType", matchDetails.Wedstrijdinformatie.Competitietype ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@Categorie", matchDetails.Wedstrijdinformatie.Categorie ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@MatchDateTime", matchDetails.Wedstrijdinformatie.Wedstrijddatetime ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@MatchDate", matchDetails.Wedstrijdinformatie.Wedstrijddatum ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@Aanvangstijd", TimeSpan.TryParse(matchDetails.Wedstrijdinformatie.Aanvangstijd, out TimeSpan aanvangstijd) ? (object)aanvangstijd : DBNull.Value);
                        command.Parameters.AddWithValue("@Duration", matchDetails.Wedstrijdinformatie.Duur ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@SpelType", matchDetails.Wedstrijdinformatie.Speltype ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@Aanduiding", matchDetails.Wedstrijdinformatie.Aanduiding ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@PouleCode", int.TryParse(matchDetails.Wedstrijdinformatie.Poulecode, out int pouleCode) ? (object)pouleCode : DBNull.Value);
                        command.Parameters.AddWithValue("@Poule", matchDetails.Wedstrijdinformatie.Poule ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ThuisTeamID", matchDetails.Wedstrijdinformatie.Thuisteamid);
                        command.Parameters.AddWithValue("@ThuisTeam", matchDetails.Wedstrijdinformatie.Thuisteam ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitTeamID", matchDetails.Wedstrijdinformatie.Uitteamid);
                        command.Parameters.AddWithValue("@UitTeam", matchDetails.Wedstrijdinformatie.Uitteam ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@Opmerkingen", matchDetails.Wedstrijdinformatie.Opmerkingen ?? (object)DBNull.Value);

                        // Match officials and kleedkamers
                        command.Parameters.AddWithValue("@VerenigingScheidsrechterCode", matchDetails.Officials.Verenigingsscheidsrechtercode ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@VerenigingScheidsrechter", matchDetails.Officials.Verenigingsscheidsrechter ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@OverigeOfficialCode", matchDetails.Officials.Overigeofficialcode ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@OverigeOfficial", matchDetails.Officials.Overigeofficial ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@Scheidsrechters", matchDetails.Matchofficials.Scheidsrechters ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@KleedkamerThuis", matchDetails.Kleedkamers.Thuis ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@KleedkamerUit", matchDetails.Kleedkamers.Uit ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@KleedkamerOfficial", matchDetails.Kleedkamers.Official ?? (object)DBNull.Value);

                        // Match accommodatie details
                        command.Parameters.AddWithValue("@AccommodatieNaam", matchDetails.Accommodatie.Naam ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@AccommodatieStraat", matchDetails.Accommodatie.Straat ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@AccommodatiePlaats", matchDetails.Accommodatie.Plaats ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@AccommodatieTelefoon", matchDetails.Accommodatie.Telefoon ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@AccommodatieRouteplanner", matchDetails.Accommodatie.Routeplanner ?? (object)DBNull.Value);

                        // Match thuisteam and uitteam details
                        command.Parameters.AddWithValue("@ThuisTeamNaam", matchDetails.Thuisteam.Naam ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ThuisTeamCode", matchDetails.Thuisteam.Code ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ThuisTeamWebsite", matchDetails.Thuisteam.Website ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ThuisTeamShirtKleur", matchDetails.Thuisteam.Shirtkleur ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ThuisTeamStraat", matchDetails.Thuisteam.Straat ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ThuisTeamPostcodePlaats", matchDetails.Thuisteam.Postcodeplaats ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ThuisTeamTelefoon", matchDetails.Thuisteam.Telefoon ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ThuisTeamEmail", matchDetails.Thuisteam.Email ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitTeamNaam", matchDetails.Uitteam.Naam ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitTeamCode", matchDetails.Uitteam.Code ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitTeamWebsite", matchDetails.Uitteam.Website ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitTeamShirtKleur", matchDetails.Uitteam.Shirtkleur ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitTeamStraat", matchDetails.Uitteam.Straat ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitTeamPostcodePlaats", matchDetails.Uitteam.Postcodeplaats ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitTeamTelefoon", matchDetails.Uitteam.Telefoon ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UitTeamEmail", matchDetails.Uitteam.Email ?? (object)DBNull.Value);

                        await command.ExecuteNonQueryAsync();
                        log.LogInformation("MATCHDETAILS - Successfully inserted stg.MatchDetails.");
                        connection.Close(); 
                    }
                }
                catch (Exception ex)
                {
                    log.LogError($"Error storing match details in database: {ex.Message}");
                }
            }
        }

        private static async Task SaveToDatabaseTeams(List<Team> teams, ILogger log)
        {
            using (SqlConnection connection = new SqlConnection(DatabaseConfig.ConnectionString))
            {
                await connection.OpenAsync();
                foreach (var team in teams)
                {
                    string query = @"INSERT INTO [stg].[teams]
                            ([teamcode]
                            ,[lokaleteamcode]
                            ,[poulecode]
                            ,[teamnaam]
                            ,[competitienaam]
                            ,[klasse]
                            ,[poule]
                            ,[klassepoule]
                            ,[spelsoort]
                            ,[competitiesoort]
                            ,[geslacht]
                            ,[teamsoort]
                            ,[leeftijdscategorie]
                            ,[kalespelsoort]
                            ,[speeldag]
                            ,[speeldagteam]
                            ,[more])
                        VALUES
                            (@teamcode
                            ,@lokaleteamcode
                            ,@poulecode
                            ,@teamnaam
                            ,@competitienaam
                            ,@klasse
                            ,@poule
                            ,@klassepoule
                            ,@spelsoort
                            ,@competitiesoort
                            ,@geslacht
                            ,@teamsoort
                            ,@leeftijdscategorie
                            ,@kalespelsoort
                            ,@speeldag
                            ,@speeldagteam
                            ,@more 
                            )";

                    using (SqlCommand cmd = new SqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@teamcode", team.teamcode);
                        cmd.Parameters.AddWithValue("@lokaleteamcode", team.lokaleteamcode); 
                        cmd.Parameters.AddWithValue("@poulecode", team.poulecode ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@teamnaam", team.teamnaam);
                        cmd.Parameters.AddWithValue("@competitienaam", team.competitienaam ?? (object)DBNull.Value); 
                        cmd.Parameters.AddWithValue("@klasse", team.klasse ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@poule", team.poule ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@klassepoule", team.klassepoule ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@spelsoort", team.spelsoort ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@competitiesoort", team.competitiesoort ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@geslacht", team.geslacht ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@teamsoort", team.teamsoort ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@leeftijdscategorie", team.leeftijdscategorie ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@kalespelsoort", team.kalespelsoort ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@speeldag", team.speeldag ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@speeldagteam", team.speeldagteam ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@more", team.more ?? (object)DBNull.Value);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                log.LogInformation("TEAMS - Data inserted into staging table.");
                connection.Close();
            }
        }

        private static async Task SaveProgrammaToStaging(List<Match> matches, ILogger log)
        {
            using (SqlConnection connection = new SqlConnection(DatabaseConfig.ConnectionString))
            {
                await connection.OpenAsync();
                int inserted = 0;
                foreach (var match in matches)
                {
                    string query = @"
                        IF NOT EXISTS (SELECT 1 FROM [stg].[matches] WHERE [wedstrijdcode] = @wedstrijdcode)
                        INSERT INTO [stg].[matches] (
                             [wedstrijddatum],[wedstrijdcode],[wedstrijdnummer],[datum],[wedstrijd]
                            ,[accommodatie],[aanvangstijd],[thuisteam],[thuisteamid],[thuisteamlogo]
                            ,[thuisteamclubrelatiecode],[uitteamclubrelatiecode],[uitteam],[uitteamid]
                            ,[uitteamlogo],[competitiesoort],[status],[meer]
                            ,[teamnaam],[teamvolgorde],[competitie],[klasse],[poule],[klassepoule]
                            ,[kaledatum],[vertrektijd],[verzameltijd],[scheidsrechters],[scheidsrechter]
                            ,[veld],[locatie],[plaats],[rijders]
                            ,[kleedkamerthuisteam],[kleedkameruitteam],[kleedkamerscheidsrechter]
                        ) VALUES (
                             @wedstrijddatum,@wedstrijdcode,@wedstrijdnummer,@datum,@wedstrijd
                            ,@accommodatie,@aanvangstijd,@thuisteam,@thuisteamid,@thuisteamlogo
                            ,@thuisteamclubrelatiecode,@uitteamclubrelatiecode,@uitteam,@uitteamid
                            ,@uitteamlogo,@competitiesoort,@status,@meer
                            ,@teamnaam,@teamvolgorde,@competitie,@klasse,@poule,@klassepoule
                            ,@kaledatum,@vertrektijd,@verzameltijd,@scheidsrechters,@scheidsrechter
                            ,@veld,@locatie,@plaats,@rijders
                            ,@kleedkamerthuisteam,@kleedkameruitteam,@kleedkamerscheidsrechter
                        )";

                    using (SqlCommand cmd = new SqlCommand(query, connection))
                    {
                        // Shared fields
                        cmd.Parameters.AddWithValue("@wedstrijddatum", match.wedstrijddatum ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@wedstrijdcode", match.wedstrijdcode);
                        cmd.Parameters.AddWithValue("@wedstrijdnummer", match.wedstrijdnummer);
                        cmd.Parameters.AddWithValue("@datum", match.datum ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@wedstrijd", match.wedstrijd ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@accommodatie", match.accommodatie ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@aanvangstijd", match.aanvangstijd ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@thuisteam", match.thuisteam ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@thuisteamid", match.thuisteamid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@thuisteamlogo", match.thuisteamlogo ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@thuisteamclubrelatiecode", match.thuisteamclubrelatiecode ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@uitteamclubrelatiecode", match.uitteamclubrelatiecode ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@uitteam", match.uitteam ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@uitteamid", match.uitteamid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@uitteamlogo", match.uitteamlogo ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@competitiesoort", match.competitiesoort ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@status", match.status ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@meer", match.meer ?? (object)DBNull.Value);
                        // /programma specific fields
                        cmd.Parameters.AddWithValue("@teamnaam", match.teamnaam ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@teamvolgorde", match.teamvolgorde);
                        cmd.Parameters.AddWithValue("@competitie", match.competitie ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@klasse", match.klasse ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@poule", match.poule ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@klassepoule", match.klassepoule ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@kaledatum", match.kaledatum ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@vertrektijd", match.vertrektijd ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@verzameltijd", match.verzameltijd ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@scheidsrechters", match.scheidsrechters ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@scheidsrechter", match.scheidsrechter ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@veld", match.veld ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@locatie", match.locatie ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@plaats", match.plaats ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@rijders", match.rijders ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@kleedkamerthuisteam", match.kleedkamerthuisteam ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@kleedkameruitteam", match.kleedkameruitteam ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@kleedkamerscheidsrechter", match.kleedkamerscheidsrechter ?? (object)DBNull.Value);
                        int rowsAffected = await cmd.ExecuteNonQueryAsync();
                        if (rowsAffected > 0) inserted++;
                    }
                }
                log.LogInformation($"MATCHES/PROGRAMMA - {inserted} new rows inserted into staging.");
                connection.Close();
            }
        }

        private static async Task MergeUitslagenIntoStaging(List<Match> matches, ILogger log)
        {
            using (SqlConnection connection = new SqlConnection(DatabaseConfig.ConnectionString))
            {
                await connection.OpenAsync();
                int updated = 0;
                foreach (var match in matches)
                {
                    // UPDATE score fields on existing programma row; INSERT full row if not present
                    string query = @"
                        IF EXISTS (SELECT 1 FROM [stg].[matches] WHERE [wedstrijdcode] = @wedstrijdcode)
                            UPDATE [stg].[matches] SET
                                 [uitslag]              = @uitslag
                                ,[uitslag-regulier]     = @uitslagregulier
                                ,[uitslag-nv]           = @uitslagnv
                                ,[uitslag-s]            = @uitslags
                                ,[datumopgemaakt]       = @datumopgemaakt
                                ,[competitienaam]       = @competitienaam
                                ,[eigenteam]            = @eigenteam
                                ,[sportomschrijving]    = @sportomschrijving
                                ,[verenigingswedstrijd] = @verenigingswedstrijd
                                ,[status]               = @status
                                ,[wedstrijddatum]       = @wedstrijddatum
                                ,[datum]                = @datum
                                ,[wedstrijd]            = @wedstrijd
                                ,[accommodatie]         = @accommodatie
                                ,[aanvangstijd]         = @aanvangstijd
                                ,[thuisteam]            = @thuisteam
                                ,[thuisteamid]          = @thuisteamid
                                ,[thuisteamlogo]        = @thuisteamlogo
                                ,[thuisteamclubrelatiecode] = @thuisteamclubrelatiecode
                                ,[uitteamclubrelatiecode]   = @uitteamclubrelatiecode
                                ,[uitteam]              = @uitteam
                                ,[uitteamid]            = @uitteamid
                                ,[uitteamlogo]          = @uitteamlogo
                                ,[competitiesoort]      = @competitiesoort
                                ,[meer]                 = @meer
                            WHERE [wedstrijdcode] = @wedstrijdcode
                        ELSE
                            INSERT INTO [stg].[matches] (
                                 [wedstrijddatum],[wedstrijdcode],[wedstrijdnummer],[datum],[wedstrijd]
                                ,[accommodatie],[aanvangstijd],[thuisteam],[thuisteamid],[thuisteamlogo]
                                ,[thuisteamclubrelatiecode],[uitteamclubrelatiecode],[uitteam],[uitteamid]
                                ,[uitteamlogo],[competitiesoort],[status],[meer]
                                ,[datumopgemaakt],[uitslag],[uitslag-regulier],[uitslag-nv],[uitslag-s]
                                ,[competitienaam],[eigenteam],[sportomschrijving],[verenigingswedstrijd]
                            ) VALUES (
                                 @wedstrijddatum,@wedstrijdcode,@wedstrijdnummer,@datum,@wedstrijd
                                ,@accommodatie,@aanvangstijd,@thuisteam,@thuisteamid,@thuisteamlogo
                                ,@thuisteamclubrelatiecode,@uitteamclubrelatiecode,@uitteam,@uitteamid
                                ,@uitteamlogo,@competitiesoort,@status,@meer
                                ,@datumopgemaakt,@uitslag,@uitslagregulier,@uitslagnv,@uitslags
                                ,@competitienaam,@eigenteam,@sportomschrijving,@verenigingswedstrijd
                            )";

                    using (SqlCommand cmd = new SqlCommand(query, connection))
                    {
                        // Shared fields
                        cmd.Parameters.AddWithValue("@wedstrijddatum", match.wedstrijddatum ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@wedstrijdcode", match.wedstrijdcode);
                        cmd.Parameters.AddWithValue("@wedstrijdnummer", match.wedstrijdnummer);
                        cmd.Parameters.AddWithValue("@datum", match.datum ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@wedstrijd", match.wedstrijd ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@accommodatie", match.accommodatie ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@aanvangstijd", match.aanvangstijd ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@thuisteam", match.thuisteam ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@thuisteamid", match.thuisteamid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@thuisteamlogo", match.thuisteamlogo ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@thuisteamclubrelatiecode", match.thuisteamclubrelatiecode ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@uitteamclubrelatiecode", match.uitteamclubrelatiecode ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@uitteam", match.uitteam ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@uitteamid", match.uitteamid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@uitteamlogo", match.uitteamlogo ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@competitiesoort", match.competitiesoort ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@status", match.status ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@meer", match.meer ?? (object)DBNull.Value);
                        // /uitslagen specific fields
                        cmd.Parameters.AddWithValue("@datumopgemaakt", match.datumopgemaakt ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@uitslag", match.uitslag ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@uitslagregulier", match.uitslag_regulier ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@uitslagnv", match.uitslag_nv ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@uitslags", match.uitslag_s ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@competitienaam", match.competitienaam ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@eigenteam", match.eigenteam ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@sportomschrijving", match.sportomschrijving ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@verenigingswedstrijd", match.verenigingswedstrijd ?? (object)DBNull.Value);

                        int rows = await cmd.ExecuteNonQueryAsync();
                        // IF EXISTS returns 1 row affected for UPDATE; ELSE returns 1 for INSERT
                        // We track by checking which branch ran via a secondary count query isn't feasible,
                        // so just track total affected rows.
                        if (rows > 0) updated++;
                    }
                }
                log.LogInformation($"MATCHES/UITSLAGEN - {updated} rows merged (updated or inserted) into staging.");
                connection.Close();
            }
        }
    }
}