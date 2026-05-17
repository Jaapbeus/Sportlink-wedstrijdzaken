using Azure.Core;
using Azure.Identity;
using Cronos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor AppSettings (zonder secrets). v2 — #87 + #27.
///
/// Endpoints:
///   GET  /api/beheer/settings          → leest niet-gevoelige AppSettings velden
///   PUT  /api/beheer/settings          → schrijft toegestane velden + logt naar dbo.AppSettingsAudit
///
/// Beveiliging:
///   - AuthorizationLevel.Function (in productie via SWA proxying veilig)
///   - SportlinkClientId en Graph secrets worden NOOIT in responses gezet
///   - Alle wijzigingen worden vastgelegd in dbo.AppSettingsAudit (CISO-eis)
///
/// Schedule-restart (#27):
///   - Bij FetchSchedule-wijziging wordt FETCH_SCHEDULE Azure App Setting bijgewerkt
///     via Azure Management API → Function App herstart automatisch
///   - Vereiste env vars: AzureSubscriptionId, AzureResourceGroupName, AzureFunctionAppName
///   - Managed Identity van de Function App heeft Website Contributor rol nodig op de Function App resource
///   - Als env vars ontbreken (bijv. lokaal): herstartVereist=true in response, handmatige herstart nodig
/// </summary>
public static class AdminSettingsFunction
{
    // Witte lijst: alleen deze velden mogen via PUT gewijzigd worden.
    // SportlinkClientId, Graph-secrets en OpenAI key komen hier NOOIT in.
    private static readonly string[] AllowedFields =
    {
        "InternDomein", "HerplanDeadlineDagen", "BufferMinuten",
        "PlannerAfzenderNaam", "CoordinatorNaam", "CoordinatorFunctie", "PlannerEmailAdres",
        "Accommodatie", "FetchSchedule", "EmailVoetnoot",
        "AccommodatiePlaats", "AccommodatieLatitude", "AccommodatieLongitude"
    };

    private const string ManagementApiVersion = "2022-03-01";

    private static readonly HttpClient _geocodeClient;
    static AdminSettingsFunction()
    {
        _geocodeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _geocodeClient.DefaultRequestHeaders.Add("User-Agent", "SportlinkAdmin/2.0");
    }

    [Function("AdminSettingsGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "beheer/settings")] HttpRequest req,
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
                    [BufferMinuten], [EmailVoetnoot], [AccommodatiePlaats],
                    [AccommodatieLatitude], [AccommodatieLongitude]
                FROM [dbo].[AppSettings]", connection);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return new NotFoundObjectResult(new { error = "Geen AppSettings rij gevonden" });

            var result = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
                result[name] = raw is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : raw;
            }

            // Voeg CRON-preview toe als FetchSchedule aanwezig is
            if (result.TryGetValue("FetchSchedule", out var sched) && sched is string schedStr && !string.IsNullOrWhiteSpace(schedStr))
            {
                result["fetchScheduleLeesbaar"] = VertaalCronNaarLeesbaar(schedStr);
                result["volgendeMomenten"] = BerekenVolgendeMomenten(schedStr, 3);
            }

            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen AppSettings");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminSettingsPut")]
    public static async Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "beheer/settings")] HttpRequest req,
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
            var changes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
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

            // Valideer CRON-expressie vóór opslaan
            if (changes.TryGetValue("FetchSchedule", out var nieuweSchedule) && nieuweSchedule != null)
            {
                if (!CronExpression.TryParse(nieuweSchedule, CronFormat.IncludeSeconds, out _))
                    return new BadRequestObjectResult(new { error = $"Ongeldige CRON-expressie: '{nieuweSchedule}'. Verwacht 6 velden (seconden minuten uren dag maand weekdag)." });
            }

            await SystemUtilities.WaitForDatabaseAsync(log);

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var currentValues = await ReadCurrentValuesAsync(connection, (SqlTransaction)transaction, changes.Keys);

                foreach (var (veld, nieuweWaarde) in changes)
                {
                    var updateCmd = new SqlCommand(
                        $"UPDATE [dbo].[AppSettings] SET [{veld}] = @Waarde",
                        connection, (SqlTransaction)transaction);
                    updateCmd.Parameters.AddWithValue("@Waarde", (object?)nieuweWaarde ?? DBNull.Value);
                    await updateCmd.ExecuteNonQueryAsync();

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
                        SystemUtilities.AppSettings.GetSetting("clubCode")
                            ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings"));
                    await auditCmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            await SystemUtilities.AppSettings.LoadSettingsAsync(log);

            var fetchScheduleChanged = changes.ContainsKey("FetchSchedule");
            string? herstartOpmerking = null;
            bool herstartAutomatisch = false;

            if (fetchScheduleChanged && nieuweSchedule != null)
            {
                var restartResult = await TriggerFunctionAppRestartAsync(nieuweSchedule, log);
                if (restartResult != null)
                {
                    herstartAutomatisch = true;
                    herstartOpmerking = restartResult;
                }
                else
                {
                    herstartOpmerking = "FetchSchedule gewijzigd — herstart van de Function App vereist om effect te laten gelden. " +
                                        "Configureer AzureSubscriptionId, AzureResourceGroupName en AzureFunctionAppName voor automatische herstart.";
                }
            }

            return new OkObjectResult(new
            {
                gewijzigdeVelden = changes.Keys.ToArray(),
                herstartVereist = fetchScheduleChanged && !herstartAutomatisch,
                herstartAutomatisch,
                opmerking = herstartOpmerking,
                fetchScheduleLeesbaar = fetchScheduleChanged && nieuweSchedule != null
                    ? VertaalCronNaarLeesbaar(nieuweSchedule) : null,
                volgendeMomenten = fetchScheduleChanged && nieuweSchedule != null
                    ? BerekenVolgendeMomenten(nieuweSchedule, 3) : null
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij opslaan AppSettings");
            return new ObjectResult(new { error = "Opslaan mislukt" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Zoekt GPS-coördinaten op voor een plaatsnaam via Nominatim (OpenStreetMap).
    /// Rate-limit: 1 req/sec per Nominatim ToS — ruim voldoende voor admin-gebruik.
    /// </summary>
    [Function("AdminGeocodeGet")]
    public static async Task<IActionResult> Geocode(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "beheer/geocode")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminGeocodeGet");
        var plaatsnaam = req.Query["plaatsnaam"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(plaatsnaam))
            return new BadRequestObjectResult(new { error = "plaatsnaam is verplicht" });
        if (plaatsnaam.Length > 100)
            return new BadRequestObjectResult(new { error = "plaatsnaam te lang (max 100 tekens)" });

        try
        {
            var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(plaatsnaam)}&format=json&limit=1&countrycodes=nl";
            var response = await _geocodeClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Nominatim antwoordde met {Status}", (int)response.StatusCode);
                return new ObjectResult(new { error = "Geocoding service tijdelijk niet beschikbaar" }) { StatusCode = 502 };
            }

            var json = await response.Content.ReadAsStringAsync();
            var results = JsonConvert.DeserializeObject<NominatimResult[]>(json);
            if (results == null || results.Length == 0)
                return new NotFoundObjectResult(new { error = $"Geen resultaat gevonden voor '{plaatsnaam}'" });

            var r = results[0];
            if (!double.TryParse(r.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(r.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                return new ObjectResult(new { error = "Ongeldige coördinaten ontvangen van geocoding service" }) { StatusCode = 502 };

            return new OkObjectResult(new { lat, lon, displayName = r.DisplayName });
        }
        catch (TaskCanceledException)
        {
            log.LogWarning("Nominatim request time-out voor '{Plaatsnaam}'", plaatsnaam);
            return new ObjectResult(new { error = "Geocoding service time-out (10s)" }) { StatusCode = 504 };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij geocoding van '{Plaatsnaam}'", plaatsnaam);
            return new ObjectResult(new { error = "Geocoding mislukt" }) { StatusCode = 500 };
        }
    }

    private class NominatimResult
    {
        [JsonProperty("lat")] public string Lat { get; set; } = "";
        [JsonProperty("lon")] public string Lon { get; set; } = "";
        [JsonProperty("display_name")] public string DisplayName { get; set; } = "";
    }

    /// <summary>
    /// Werkt de FETCH_SCHEDULE Azure App Setting bij via de Azure Management API.
    /// Azure herstart de Function App automatisch bij app setting wijziging.
    /// Vereist: AzureSubscriptionId, AzureResourceGroupName, AzureFunctionAppName env vars
    ///          en Managed Identity met Website Contributor rol op de Function App.
    /// Geeft null terug als de env vars niet geconfigureerd zijn (lokale omgeving).
    /// </summary>
    private static async Task<string?> TriggerFunctionAppRestartAsync(string nieuweSchedule, ILogger log)
    {
        var subscriptionId = Environment.GetEnvironmentVariable("AzureSubscriptionId");
        var resourceGroup  = Environment.GetEnvironmentVariable("AzureResourceGroupName");
        var functionAppName = Environment.GetEnvironmentVariable("AzureFunctionAppName");

        if (string.IsNullOrWhiteSpace(subscriptionId) ||
            string.IsNullOrWhiteSpace(resourceGroup) ||
            string.IsNullOrWhiteSpace(functionAppName))
        {
            log.LogWarning("Azure Management env vars niet geconfigureerd (AzureSubscriptionId / AzureResourceGroupName / AzureFunctionAppName) — automatische herstart overgeslagen");
            return null;
        }

        try
        {
            var credential = new DefaultAzureCredential();
            var tokenContext = new TokenRequestContext(["https://management.azure.com/.default"]);
            var token = await credential.GetTokenAsync(tokenContext);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            var baseUrl = $"https://management.azure.com/subscriptions/{subscriptionId}" +
                          $"/resourceGroups/{resourceGroup}" +
                          $"/providers/Microsoft.Web/sites/{functionAppName}";

            // Haal huidige app settings op (POST .../config/appsettings/list)
            var listResponse = await http.PostAsync($"{baseUrl}/config/appsettings/list?api-version={ManagementApiVersion}", null);
            listResponse.EnsureSuccessStatusCode();
            var listJson = await listResponse.Content.ReadAsStringAsync();
            var listObj = JObject.Parse(listJson);

            var properties = new Dictionary<string, string?>();
            var existingProps = listObj["properties"] as JObject;
            if (existingProps != null)
            {
                foreach (var prop in existingProps.Properties())
                    properties[prop.Name] = prop.Value.ToString();
            }

            // Bijwerken FETCH_SCHEDULE
            properties["FETCH_SCHEDULE"] = nieuweSchedule;

            var putBody = JsonConvert.SerializeObject(new { properties });
            var putResponse = await http.PutAsync(
                $"{baseUrl}/config/appsettings?api-version={ManagementApiVersion}",
                new StringContent(putBody, Encoding.UTF8, "application/json"));
            putResponse.EnsureSuccessStatusCode();

            log.LogInformation("FETCH_SCHEDULE bijgewerkt naar '{Schedule}' via Azure Management API — Function App herstart automatisch", nieuweSchedule);
            return "FetchSchedule bijgewerkt. De Function App herstart automatisch en het nieuwe ophaalschema is actief na de herstart.";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij aanroepen Azure Management API voor herstart");
            return null;
        }
    }

    /// <summary>
    /// Vertaalt veelgebruikte CRON-expressies naar leesbare Nederlandse tekst.
    /// Valt terug op de ruwe expressie als het patroon niet herkend wordt.
    /// </summary>
    internal static string VertaalCronNaarLeesbaar(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return cron;
        var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6) return cron;

        var (sec, min, uur, dag, maand, week) = (parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]);

        // Dagelijks op vaste tijd: "0 MM HH * * *"
        if (sec == "0" && dag == "*" && maand == "*" && week == "*"
            && int.TryParse(min, out var m) && int.TryParse(uur, out var h))
            return $"Elke dag om {h:D2}:{m:D2}";

        // Elk uur op vaste minuut: "0 MM * * * *"
        if (sec == "0" && uur == "*" && dag == "*" && maand == "*" && week == "*"
            && int.TryParse(min, out var hmin))
            return $"Elk uur op minuut :{hmin:D2}";

        // Elke N minuten: "0 */N * * * *"
        if (sec == "0" && uur == "*" && dag == "*" && maand == "*" && week == "*"
            && min.StartsWith("*/") && int.TryParse(min[2..], out var interval))
            return $"Elke {interval} minuten";

        // Maandelijks op vaste dag+tijd: "0 MM HH D * *"
        if (sec == "0" && maand == "*" && week == "*"
            && int.TryParse(min, out var mm) && int.TryParse(uur, out var hh) && int.TryParse(dag, out var dd))
            return $"Maandelijks op dag {dd} om {hh:D2}:{mm:D2}";

        return cron;
    }

    /// <summary>
    /// Berekent de eerstvolgende N uitvoertijden voor een CRON-expressie (6-veld Azure-formaat).
    /// Geeft een lege lijst als de expressie ongeldig is.
    /// </summary>
    internal static List<string> BerekenVolgendeMomenten(string cron, int aantal)
    {
        var resultaten = new List<string>();
        if (!CronExpression.TryParse(cron, CronFormat.IncludeSeconds, out var expr)) return resultaten;

        var nu = DateTime.UtcNow;
        var volgende = nu;
        for (int i = 0; i < aantal; i++)
        {
            var next = expr.GetNextOccurrence(volgende, TimeZoneInfo.Utc);
            if (next == null) break;
            resultaten.Add(next.Value.ToString("yyyy-MM-ddTHH:mm:ss"));
            volgende = next.Value;
        }
        return resultaten;
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
