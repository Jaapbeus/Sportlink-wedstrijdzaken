using System;
using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction
{
    public static class SystemUtilities
    {
        public static class AppSettings
        {
            private static readonly ConcurrentDictionary<string, string> settings = new ConcurrentDictionary<string, string>();

            public static async Task LoadSettingsAsync(ILogger log)
            {
                try
                {
                    using (SqlConnection connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString))
                    {
                        await connection.OpenAsync();
                        // SyncEnabled bestaat pas na migratie #324. Dynamisch controleren of de
                        // kolom aanwezig is zodat de query bij oudere DB-installaties ook werkt.
                        using var syncCheckCmd = new SqlCommand(
                            "SELECT COUNT(1) FROM sys.columns WHERE object_id = OBJECT_ID('[dbo].[AppSettings]') AND name = 'SyncEnabled'",
                            connection);
                        var hasSyncEnabled = (int)(await syncCheckCmd.ExecuteScalarAsync() ?? 0) > 0;
                        var syncFilter = hasSyncEnabled ? "WHERE [SyncEnabled] = 1" : "";

                        string query = $@"
                            SELECT TOP 1 [ClubName], [ClubCode], [SportlinkApiUrl], [SportlinkClientId],
                                   [SeasonStartMonth], [LastSyncTimestamp], [FetchSchedule],
                                   [PlannerAfzenderNaam], [CoordinatorNaam], [CoordinatorFunctie],
                                   [PlannerEmailAdres], [Accommodatie],
                                   [HerplanDeadlineDagen], [BufferMinuten],
                                   [AccommodatieLatitude], [AccommodatieLongitude], [EmailVoetnoot],
                                   [AccommodatiePlaats]
                            FROM [dbo].[AppSettings]
                            {syncFilter}";
                        using (SqlCommand command = new SqlCommand(query, connection))
                        using (SqlDataReader reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                void Set(string key, int col) {
                                    if (!reader.IsDBNull(col))
                                        settings[key] = reader.GetValue(col).ToString() ?? "";
                                }

                                settings["clubName"]        = reader.IsDBNull(0) ? "" : reader.GetString(0);
                                settings["clubCode"]        = reader.IsDBNull(1) ? "" : reader.GetString(1);
                                settings["sportlinkApiUrl"] = reader.IsDBNull(2) ? "" : reader.GetString(2);
                                settings["sportlinkClientId"] = reader.IsDBNull(3) ? "" : reader.GetString(3);
                                Set("seasonStartMonth", 4);
                                if (!reader.IsDBNull(5))
                                    settings["lastSyncTimestamp"] = Convert.ToDateTime(reader.GetValue(5)).ToString("yyyy-MM-dd HH:mm:ss");
                                settings["fetchSchedule"]   = reader.IsDBNull(6) ? "0 0 4 * * *" : reader.GetString(6);
                                Set("plannerAfzenderNaam", 7);
                                Set("coordinatorNaam", 8);
                                Set("coordinatorFunctie", 9);
                                Set("plannerEmailAdres", 10);
                                Set("accommodatie", 11);
                                Set("herplanDeadlineDagen", 12);
                                Set("bufferMinuten", 13);
                                Set("accommodatieLatitude", 14);
                                Set("accommodatieLongitude", 15);
                                Set("emailVoetnoot", 16);
                                Set("accommodatiePlaats", 17);
                            }
                        }

                        // UseRealtimeApi: kolom bestaat pas na DB-migratie — dynamisch laden zodat
                        // de Function App ook opstart als de kolom nog niet aangemaakt is.
                        using var rtaCmd = new SqlCommand(@"
                            DECLARE @v BIT = 1;
                            DECLARE @sql NVARCHAR(200) = CASE
                                WHEN COL_LENGTH('[dbo].[AppSettings]', 'UseRealtimeApi') IS NOT NULL
                                THEN N'SELECT TOP 1 @v = [UseRealtimeApi] FROM [dbo].[AppSettings]'
                                ELSE N'SELECT @v = CAST(1 AS BIT)'
                            END;
                            EXEC sp_executesql @sql, N'@v BIT OUTPUT', @v = @v OUTPUT;
                            SELECT @v;", connection);
                        var rtaResult = await rtaCmd.ExecuteScalarAsync();
                        settings["useRealtimeApi"] = (rtaResult is bool b && !b) ? "0" : "1";
                    }
                    log.LogInformation("App settings loaded successfully.");
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Error loading app settings");
                }
            }

            public static string? GetSetting(string key)
            {
                return settings.TryGetValue(key, out var value) ? value : null;
            }

            public static async Task SaveLastSyncTimestampAsync(ILogger log)
            {
                try
                {
                    using var connection = new SqlConnection(DatabaseConfig.ConnectionString);
                    await connection.OpenAsync();
                    using var command = new SqlCommand(@"
                        UPDATE [dbo].[AppSettings]
                        SET [LastSyncTimestamp] = GETUTCDATE()
                        WHERE [ClubCode] = (SELECT TOP 1 [ClubCode] FROM [dbo].[AppSettings]
                                            WHERE COL_LENGTH('[dbo].[AppSettings]', 'SyncEnabled') IS NULL
                                               OR [SyncEnabled] = 1)", connection);
                    await command.ExecuteNonQueryAsync();
                    log.LogInformation("Last sync timestamp updated.");
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Error saving last sync timestamp");
                }
            }
        }
        public static class DatabaseConfig
        {
            public static readonly string ConnectionString = Environment.GetEnvironmentVariable("SqlConnectionString") ?? throw new InvalidOperationException("The connection string is not set in the environment variables.");
        }

        public static async Task WaitForDatabaseAsync(ILogger log)
        {
            bool isDatabaseAvailable = false;
            int retryCount = 0;
            int maxRetries = 20;
            int delayBetweenRetries = 15000; // 15 seconds — Azure SQL Serverless auto-resume takes 30-90s; 20 retries = 5 min total

            while (!isDatabaseAvailable && retryCount < maxRetries)
            {
                try
                {
                    using (SqlConnection connection = new SqlConnection(DatabaseConfig.ConnectionString))
                    {
                        await connection.OpenAsync();
                        isDatabaseAvailable = true;
                        log.LogInformation("Database connection established.");
                    }
                    await AppSettings.LoadSettingsAsync(log);
                }
                catch (Exception ex)
                {
                    retryCount++;
                    log.LogWarning(ex, "Database connection failed. Retry {RetryCount}/{MaxRetries}", retryCount, maxRetries);
                    if (retryCount < maxRetries)
                        await Task.Delay(delayBetweenRetries);
                }
            }

            if (!isDatabaseAvailable)
            {
                throw new Exception("Unable to establish a database connection after multiple attempts.");
            }
        }

        /// <summary>
        /// Berekent een deterministische 12-karakter hex fingerprint voor een exception.
        /// Identieke fouten (zelfde type, genormaliseerd bericht, zelfde callsite) geven altijd
        /// dezelfde fingerprint — essentieel voor deduplicatie van GitHub Issues.
        /// </summary>
        public static string ComputeFingerprint(Exception ex)
        {
            var raw = $"{ex.GetType().FullName}|{NormalizeMessage(ex.Message)}|{GetCallerFrame(ex)}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes)[..12].ToLower();
        }

        private static string NormalizeMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            // Verwijder variabele delen zodat dezelfde fout altijd dezelfde fingerprint geeft
            var s = message;
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", "<guid>");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2})?", "<date>");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\b\d+\b", "<n>");
            return s.Trim();
        }

        private static string GetCallerFrame(Exception ex)
        {
            if (ex.StackTrace == null) return "unknown";
            foreach (var line in ex.StackTrace.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("at SportlinkFunction.", StringComparison.Ordinal))
                    return trimmed.Split('(')[0].Replace("at ", "").Trim();
            }
            return "external";
        }

        public static class SeasonHelper
        {
            /// <summary>
            /// Returns the number of weeks from today to the end of the latest season in dbo.Season.
            /// </summary>
            public static async Task<int> GetSeasonEndWeekOffsetAsync(ILogger log)
            {
                try
                {
                    using var connection = new SqlConnection(DatabaseConfig.ConnectionString);
                    await connection.OpenAsync();
                    using var command = new SqlCommand("SELECT MAX(DateUntil) FROM [dbo].[Season]", connection);
                    var result = await command.ExecuteScalarAsync();
                    if (result != null && result != DBNull.Value)
                    {
                        var endDate = Convert.ToDateTime(result);
                        return (int)Math.Ceiling((endDate - DateTime.Today).TotalDays / 7.0);
                    }
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Error fetching season end date");
                }
                return 30; // Fallback: ~30 weeks ahead
            }

            /// <summary>
            /// Returns the week offset from today to the start of the season whose DateFrom year matches startYear.
            /// The returned value is negative when the season start is in the past.
            /// </summary>
            public static async Task<int> GetSeasonStartWeekOffsetAsync(int startYear, ILogger log)
            {
                try
                {
                    using var connection = new SqlConnection(DatabaseConfig.ConnectionString);
                    await connection.OpenAsync();
                    using var command = new SqlCommand(
                        "SELECT MIN(DateFrom) FROM [dbo].[Season] WHERE YEAR(DateFrom) = @Year", connection);
                    command.Parameters.AddWithValue("@Year", startYear);
                    var result = await command.ExecuteScalarAsync();
                    if (result != null && result != DBNull.Value)
                    {
                        var startDate = Convert.ToDateTime(result);
                        return (int)Math.Floor((startDate - DateTime.Today).TotalDays / 7.0);
                    }
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Error fetching season start for year {StartYear}", startYear);
                }
                return -40; // Fallback: ~40 weeks back
            }
        }
    }
}