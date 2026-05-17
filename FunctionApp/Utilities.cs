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
                        string query = @"
                            SELECT [ClubName], [ClubCode], [SportlinkApiUrl], [SportlinkClientId],
                                   [SeasonStartMonth], [LastSyncTimestamp], [FetchSchedule],
                                   [PlannerAfzenderNaam], [CoordinatorNaam], [CoordinatorFunctie],
                                   [PlannerEmailAdres], [Accommodatie], [InternDomein],
                                   [HerplanDeadlineDagen], [BufferMinuten],
                                   [AccommodatieLatitude], [AccommodatieLongitude], [EmailVoetnoot]
                            FROM [dbo].[AppSettings]";
                        using (SqlCommand command = new SqlCommand(query, connection))
                        using (SqlDataReader reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
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
                                Set("internDomein", 12);
                                Set("herplanDeadlineDagen", 13);
                                Set("bufferMinuten", 14);
                                Set("accommodatieLatitude", 15);
                                Set("accommodatieLongitude", 16);
                                Set("emailVoetnoot", 17);
                            }
                        }
                    }
                    log.LogInformation("App settings loaded successfully.");
                }
                catch (Exception ex)
                {
                    log.LogError($"Error loading app settings: {ex.Message}");
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
                    using var command = new SqlCommand(
                        "UPDATE [dbo].[AppSettings] SET [LastSyncTimestamp] = GETDATE()", connection);
                    await command.ExecuteNonQueryAsync();
                    log.LogInformation("Last sync timestamp updated.");
                }
                catch (Exception ex)
                {
                    log.LogError($"Error saving last sync timestamp: {ex.Message}");
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
            int maxRetries = 10;
            int delayBetweenRetries = 15000; // 15 seconds — Azure SQL Serverless auto-resume takes 30-90s

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
                    log.LogWarning($"Database connection failed. Retry {retryCount}/{maxRetries}. Error: {ex.Message}");
                    if (retryCount < maxRetries)
                        await Task.Delay(delayBetweenRetries);
                }
            }

            if (!isDatabaseAvailable)
            {
                throw new Exception("Unable to establish a database connection after multiple attempts.");
            }
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
                    log.LogError($"Error fetching season end date: {ex.Message}");
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
                    log.LogError($"Error fetching season start for year {startYear}: {ex.Message}");
                }
                return -40; // Fallback: ~40 weeks back
            }
        }
    }
}