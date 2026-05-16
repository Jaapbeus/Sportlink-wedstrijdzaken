using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor AppSettings (zonder secrets). v2 — #87 + #27.
///
/// Endpoints:
///   GET  /api/admin/settings          → leest niet-gevoelige AppSettings velden
///   PUT  /api/admin/settings          → schrijft toegestane velden + logt naar dbo.AppSettingsAudit
///
/// Beveiliging:
///   - AuthorizationLevel.Function (in productie via SWA proxying veilig)
///   - SportlinkClientId en Graph secrets worden NOOIT in responses gezet
///   - Alle wijzigingen worden vastgelegd in dbo.AppSettingsAudit (CISO-eis)
/// </summary>
public static class AdminSettingsFunction
{
    // Witte lijst: alleen deze velden mogen via PUT gewijzigd worden.
    // SportlinkClientId, Graph-secrets en OpenAI key komen hier NOOIT in.
    private static readonly string[] AllowedFields =
    {
        "InternDomein", "HerplanDeadlineDagen", "BufferMinuten",
        "PlannerAfzenderNaam", "CoordinatorNaam", "CoordinatorFunctie", "PlannerEmailAdres",
        "Accommodatie", "FetchSchedule"
    };

    [Function("AdminSettingsGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "admin/settings")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSettingsGet");
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            // Geen SportlinkClientId, geen geheimen
            using var command = new SqlCommand(@"
                SELECT TOP 1
                    [ClubName], [ClubCode], [SportlinkApiUrl], [SeasonStartMonth], [Accommodatie],
                    [LastSyncTimestamp], [FetchSchedule], [PlannerAfzenderNaam], [CoordinatorNaam],
                    [CoordinatorFunctie], [PlannerEmailAdres], [InternDomein], [HerplanDeadlineDagen],
                    [BufferMinuten]
                FROM [dbo].[AppSettings]", connection);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return new NotFoundObjectResult(new { error = "Geen AppSettings rij gevonden" });

            var result = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                result[name] = value;
            }

            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen AppSettings");
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    [Function("AdminSettingsPut")]
    public static async Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "admin/settings")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSettingsPut");
        try
        {
            using var bodyReader = new StreamReader(req.Body);
            var bodyText = await bodyReader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(bodyText))
                return new BadRequestObjectResult(new { error = "Lege request body" });

            var updateRequest = JsonConvert.DeserializeObject<UpdateSettingsRequest>(bodyText);
            if (updateRequest == null)
                return new BadRequestObjectResult(new { error = "Ongeldige JSON" });

            var gewijzigdDoor = updateRequest.GewijzigdDoor ?? req.Query["gewijzigdDoor"].ToString();
            if (string.IsNullOrWhiteSpace(gewijzigdDoor)) gewijzigdDoor = "onbekend";

            // Pluk alleen de toegestane velden — alles erbuiten wordt genegeerd
            var changes = new Dictionary<string, string?>();
            if (updateRequest.Velden != null)
            {
                foreach (var (key, value) in updateRequest.Velden)
                {
                    if (AllowedFields.Contains(key, StringComparer.OrdinalIgnoreCase))
                        changes[key] = value;
                    else
                        log.LogWarning("AdminSettingsPut: veld {Veld} niet in witte lijst, wordt genegeerd", key);
                }
            }

            if (changes.Count == 0)
                return new BadRequestObjectResult(new { error = "Geen toegestane velden in request" });

            await SystemUtilities.WaitForDatabaseAsync(log);

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // Lees huidige waarden voor de audit-trail
                var currentValues = await ReadCurrentValuesAsync(connection, (SqlTransaction)transaction, changes.Keys);

                // UPDATE met dynamische SET-clause (gebruik geparameteriseerde queries per veld)
                foreach (var (veld, nieuweWaarde) in changes)
                {
                    var updateCmd = new SqlCommand(
                        $"UPDATE [dbo].[AppSettings] SET [{veld}] = @Waarde",
                        connection, (SqlTransaction)transaction);
                    updateCmd.Parameters.AddWithValue("@Waarde", (object?)nieuweWaarde ?? DBNull.Value);
                    await updateCmd.ExecuteNonQueryAsync();

                    // Audit-log entry
                    currentValues.TryGetValue(veld, out var oud);
                    var auditCmd = new SqlCommand(@"
                        INSERT INTO [dbo].[AppSettingsAudit]
                            ([GewijzigdDoor], [Veld], [OudeWaarde], [NieuweWaarde], [ClubCode])
                        VALUES (@GewijzigdDoor, @Veld, @OudeWaarde, @NieuweWaarde, @ClubCode)",
                        connection, (SqlTransaction)transaction);
                    auditCmd.Parameters.AddWithValue("@GewijzigdDoor", gewijzigdDoor);
                    auditCmd.Parameters.AddWithValue("@Veld", veld);
                    auditCmd.Parameters.AddWithValue("@OudeWaarde", (object?)oud ?? DBNull.Value);
                    auditCmd.Parameters.AddWithValue("@NieuweWaarde", (object?)nieuweWaarde ?? DBNull.Value);
                    auditCmd.Parameters.AddWithValue("@ClubCode",
                        SystemUtilities.AppSettings.GetSetting("clubCode") ?? "VRC");
                    await auditCmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            // Refresh de in-memory cache
            await SystemUtilities.AppSettings.LoadSettingsAsync(log);

            var fetchScheduleChanged = changes.Keys.Any(k =>
                string.Equals(k, "FetchSchedule", StringComparison.OrdinalIgnoreCase));

            return new OkObjectResult(new
            {
                gewijzigdeVelden = changes.Keys.ToArray(),
                herstartVereist = fetchScheduleChanged,
                opmerking = fetchScheduleChanged
                    ? "FetchSchedule gewijzigd — herstart van de Function App vereist om effect te laten gelden"
                    : null
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij opslaan AppSettings");
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    private static async Task<Dictionary<string, string?>> ReadCurrentValuesAsync(
        SqlConnection connection, SqlTransaction transaction, IEnumerable<string> velden)
    {
        var safe = velden.Where(v => AllowedFields.Contains(v, StringComparer.OrdinalIgnoreCase))
                         .Select(v => $"[{v}]")
                         .ToList();
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (safe.Count == 0) return result;

        var sql = $"SELECT TOP 1 {string.Join(", ", safe)} FROM [dbo].[AppSettings]";
        using var cmd = new SqlCommand(sql, connection, transaction);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                result[name] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
            }
        }
        return result;
    }

    public class UpdateSettingsRequest
    {
        public string? GewijzigdDoor { get; set; }
        public Dictionary<string, string?>? Velden { get; set; }
    }
}
