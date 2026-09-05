using Azure.Core;
using Azure.Identity;
using Cronos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminSettingsFunction.cs</c> (#887).
/// <para>
/// Vertaling: <c>[dbo].[AppSettings]</c>/<c>[dbo].[AppSettingsAudit]</c> →
/// <c>public.appsettings</c>/<c>public.appsettingsaudit</c>, <c>SELECT TOP 1</c> → <c>LIMIT 1</c>.
/// Elke reader-gevoede responsdictionary gebruikt gequote PascalCase-aliassen (#855-casing-regel) —
/// zonder die aliassen zou de GET-response volledig lowercase kolomnamen teruggeven en zou de
/// Blazor Admin GUI leeg renderen.
/// </para>
/// <para>
/// <b>Dynamische kolomselectie (<c>AllowedFields</c>-whitelist + regex):</b> de C#-validatie
/// (whitelist-check + <c>^[A-Za-z][A-Za-z0-9_]*$</c>) blijft ongewijzigd. De geïnterpoleerde
/// kolomnaam wordt bewust NIET gequote — Postgres vouwt een ongequote identifier altijd naar
/// lowercase, dus <c>SET HerplanDeadlineDagen = …</c> matcht de echte kolom
/// <c>herplandeadlinedagen</c> zonder dat er een aparte PascalCase→lowercase-mapping nodig is.
/// </para>
/// <para>
/// <b>Bewust vereenvoudigd t.o.v. de SQL Server-tier:</b> de <c>COL_LENGTH</c>/<c>sp_executesql</c>-
/// omweg rond <c>UseRealtimeApi</c> bestond omdat die kolom pas ná een latere migratie bestond op
/// bestaande installaties. Op de Postgres-tier wordt elke migratie in volgorde en idempotent
/// toegepast door <c>MigrationRunner</c> vóórdat de applicatie start — "de kolom bestaat misschien
/// nog niet" is hier geen realistische runtime-toestand, dus een gewone <c>SELECT</c> volstaat.
/// </para>
/// </summary>
public static class AdminSettingsFunction
{
    private static readonly string[] AllowedFields =
    {
        "HerplanDeadlineDagen", "BufferMinuten",
        "PlannerAfzenderNaam", "CoordinatorNaam", "CoordinatorFunctie", "PlannerEmailAdres",
        "Accommodatie", "FetchSchedule", "EmailVoetnoot",
        "AccommodatiePlaats", "AccommodatieLatitude", "AccommodatieLongitude",
        "UseRealtimeApi", "KnvbPdfBijlageIngeschakeld", "KnvbStandaardRegio",
        "SportlinkExtensionEnabled"
    };

    private static readonly string[] GeldigeKnvbRegios =
    {
        "West", "Noord", "Oost", "Zuid", "Landelijk", "LandelijkJeugd"
    };

    /// <summary>
    /// Postgres-specifieke toevoeging: <c>changes</c>-waarden komen altijd als <c>string?</c> binnen
    /// (de JSON-request is <c>Dictionary&lt;string,string?&gt;</c>). SQL Server accepteert een
    /// tekst-parameter in een <c>INT</c>/<c>BIT</c>/<c>FLOAT</c>-kolom via impliciete conversie;
    /// Postgres doet dat niet (<c>42804 column … is of type integer but expression is of type
    /// text</c>) — vandaag empirisch aangetroffen bij <c>BufferMinuten</c>. Elke niet-tekstkolom
    /// krijgt daarom een expliciete <c>::type</c>-cast in de UPDATE-SQL.
    /// </summary>
    private static readonly Dictionary<string, string> FieldCasts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HerplanDeadlineDagen"] = "::integer",
        ["BufferMinuten"] = "::integer",
        ["AccommodatieLatitude"] = "::double precision",
        ["AccommodatieLongitude"] = "::double precision",
        ["UseRealtimeApi"] = "::boolean",
        ["KnvbPdfBijlageIngeschakeld"] = "::boolean",
        ["SportlinkExtensionEnabled"] = "::boolean",
    };

    private const string ManagementApiVersion = "2022-03-01";

    private static readonly HttpClient _geocodeClient;
    static AdminSettingsFunction()
    {
        _geocodeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(AdminEndpoint.OutboundHttpTimeoutSeconds) };
        _geocodeClient.DefaultRequestHeaders.Add("User-Agent", AdminEndpoint.OutboundUserAgent);
    }

    [Function("AdminSettingsGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/settings")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSettingsGet");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            await using var command = new NpgsqlCommand(@"
                SELECT
                    clubname AS ""ClubName"", clubcode AS ""ClubCode"",
                    sportlinkapiurl AS ""SportlinkApiUrl"", seasonstartmonth AS ""SeasonStartMonth"",
                    accommodatie AS ""Accommodatie"", lastsynctimestamp AS ""LastSyncTimestamp"",
                    fetchschedule AS ""FetchSchedule"", plannerafzendernaam AS ""PlannerAfzenderNaam"",
                    coordinatornaam AS ""CoordinatorNaam"", coordinatorfunctie AS ""CoordinatorFunctie"",
                    planneremailadres AS ""PlannerEmailAdres"",
                    herplandeadlinedagen AS ""HerplanDeadlineDagen"", bufferminuten AS ""BufferMinuten"",
                    emailvoetnoot AS ""EmailVoetnoot"", accommodatieplaats AS ""AccommodatiePlaats"",
                    accommodatielatitude AS ""AccommodatieLatitude"",
                    accommodatielongitude AS ""AccommodatieLongitude"",
                    knvbpdfbijlageingeschakeld AS ""KnvbPdfBijlageIngeschakeld"",
                    knvbstandaardregio AS ""KnvbStandaardRegio"",
                    userealtimeapi AS ""UseRealtimeApi"",
                    sportlinkextensionenabled AS ""SportlinkExtensionEnabled""
                FROM public.appsettings
                WHERE clubcode = @clubcode
                LIMIT 1", connection);
            command.Parameters.AddWithValue("clubcode", clubCode);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return new NotFoundObjectResult(new { error = "Geen AppSettings rij gevonden" });

            var result = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                result[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            reader.Close();

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
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/settings")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSettingsPut");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
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

            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

            var validatieFout = ValidateAndFilterChanges(updateRequest, log, out var changes);
            if (validatieFout != null) return validatieFout;
            changes.TryGetValue("FetchSchedule", out var nieuweSchedule);

            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            await ApplyChangesAsync(connection, changes, gewijzigdDoor, clubCode);

            await PostgresAppSettings.LoadSettingsAsync(log);

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

    private static IActionResult? ValidateAndFilterChanges(
        UpdateSettingsRequest updateRequest, ILogger log, out Dictionary<string, string?> changes)
    {
        changes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (updateRequest.Velden != null)
        {
            foreach (var (key, value) in updateRequest.Velden)
            {
                if (!AllowedFields.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    log.LogWarning("AdminSettingsPut: veld {Veld} niet in witte lijst, wordt genegeerd", key);
                    continue;
                }
                if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Za-z][A-Za-z0-9_]*$"))
                {
                    log.LogWarning("AdminSettingsPut: veldnaam {Veld} bevat ongeldige tekens, wordt genegeerd", key);
                    continue;
                }
                changes[key] = value;
            }
        }

        if (changes.Count == 0)
            return new BadRequestObjectResult(new { error = "Geen toegestane velden in request" });

        if (changes.TryGetValue("FetchSchedule", out var nieuweSchedule) && nieuweSchedule != null)
        {
            if (!CronExpression.TryParse(nieuweSchedule, CronFormat.IncludeSeconds, out _))
                return new BadRequestObjectResult(new { error = $"Ongeldige CRON-expressie: '{nieuweSchedule}'. Verwacht 6 velden (seconden minuten uren dag maand weekdag)." });
        }

        if (changes.TryGetValue("KnvbStandaardRegio", out var nieuweRegio) &&
            !string.IsNullOrWhiteSpace(nieuweRegio) &&
            !GeldigeKnvbRegios.Contains(nieuweRegio, StringComparer.Ordinal))
        {
            return new BadRequestObjectResult(new { error = $"Ongeldige KnvbStandaardRegio: '{nieuweRegio}'. Toegestaan: {string.Join(", ", GeldigeKnvbRegios)}." });
        }

        return null;
    }

    private static async Task ApplyChangesAsync(
        NpgsqlConnection connection, Dictionary<string, string?> changes, string gewijzigdDoor, string clubCode)
    {
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var currentValues = await ReadCurrentValuesAsync(connection, transaction, changes.Keys, clubCode);

            foreach (var (veld, nieuweWaarde) in changes)
            {
                var cast = FieldCasts.GetValueOrDefault(veld, "");
                var updateCmd = new NpgsqlCommand(
                    $"UPDATE public.appsettings SET {veld} = @waarde{cast} WHERE clubcode = @clubcode",
                    connection, transaction);
                updateCmd.Parameters.AddWithValue("waarde", (object?)nieuweWaarde ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("clubcode", clubCode);
                await updateCmd.ExecuteNonQueryAsync();
                await updateCmd.DisposeAsync();

                currentValues.TryGetValue(veld, out var oud);
                var auditCmd = new NpgsqlCommand(@"
                    INSERT INTO public.appsettingsaudit
                        (gewijzigddoor, veld, oudewaarde, nieuwewaarde, clubcode)
                    VALUES (@gewijzigddoor, @veld, @oudewaarde, @nieuwewaarde, @clubcode)",
                    connection, transaction);
                auditCmd.Parameters.AddWithValue("gewijzigddoor", gewijzigdDoor);
                auditCmd.Parameters.AddWithValue("veld", veld);
                auditCmd.Parameters.AddWithValue("oudewaarde", (object?)oud ?? DBNull.Value);
                auditCmd.Parameters.AddWithValue("nieuwewaarde", (object?)nieuweWaarde ?? DBNull.Value);
                auditCmd.Parameters.AddWithValue("clubcode", clubCode);
                await auditCmd.ExecuteNonQueryAsync();
                await auditCmd.DisposeAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    [Function("AdminGeocodeGet")]
    public static async Task<IActionResult> Geocode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/geocode")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminGeocodeGet");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
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

    internal static string VertaalCronNaarLeesbaar(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return cron;
        var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6) return cron;

        var (sec, min, uur, dag, maand, week) = (parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]);

        if (sec == "0" && dag == "*" && maand == "*" && week == "*"
            && int.TryParse(min, out var m) && int.TryParse(uur, out var h))
            return $"Elke dag om {h:D2}:{m:D2}";

        if (sec == "0" && uur == "*" && dag == "*" && maand == "*" && week == "*"
            && int.TryParse(min, out var hmin))
            return $"Elk uur op minuut :{hmin:D2}";

        if (sec == "0" && uur == "*" && dag == "*" && maand == "*" && week == "*"
            && min.StartsWith("*/") && int.TryParse(min[2..], out var interval))
            return $"Elke {interval} minuten";

        if (sec == "0" && maand == "*" && week == "*"
            && int.TryParse(min, out var mm) && int.TryParse(uur, out var hh) && int.TryParse(dag, out var dd))
            return $"Maandelijks op dag {dd} om {hh:D2}:{mm:D2}";

        return cron;
    }

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
        NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<string> velden, string clubCode)
    {
        var safe = velden.Where(v => AllowedFields.Contains(v, StringComparer.OrdinalIgnoreCase))
                         .Select(v => $"{v} AS \"{v}\"")
                         .ToList();
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (safe.Count == 0) return result;

        var sql = $"SELECT {string.Join(", ", safe)} FROM public.appsettings WHERE clubcode = @clubcode LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        await using var reader = await cmd.ExecuteReaderAsync();
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
