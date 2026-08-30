using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminTemplatesFunction.cs</c> (#887).
/// Vertaling: <c>[dbo].[EmailTemplateInstellingen]</c>/<c>[dbo].[AppSettingsAudit]</c> →
/// <c>public.emailtemplateinstellingen</c>/<c>public.appsettingsaudit</c>,
/// <c>MERGE ... WHEN MATCHED/NOT MATCHED</c> → <c>INSERT ... ON CONFLICT (templatekey, clubcode)
/// DO UPDATE SET</c>.
/// <para>
/// <b>Bewust weggelaten t.o.v. de SQL Server-tier:</b> de aanroep naar
/// <c>EmailTemplateService.InvalidateCache()</c>. Die service — en de e-mailverwerkingspijplijn die
/// er gebruik van maakt — bestaat nog niet op de Postgres-tier (dat is #889's scope); een cache
/// invalideren die nergens leest is een no-op die alleen een niet-bestaande afhankelijkheid zou
/// toevoegen. Terug te zetten zodra #889 die service levert.
/// </para>
/// </summary>
public static class AdminTemplatesFunction
{
    [Function("AdminTemplatesGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/templates")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTemplatesGet");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(@"
                SELECT id AS ""Id"", templatekey AS ""TemplateKey"", onderwerp AS ""Onderwerp"",
                       bodytemplate AS ""BodyTemplate"", actief AS ""Actief"", clubcode AS ""ClubCode"",
                       mta_inserted, mta_modified
                FROM public.emailtemplateinstellingen
                WHERE clubcode = @clubcode
                ORDER BY templatekey", connection);
            command.Parameters.AddWithValue("clubcode", clubCode);

            await using var reader = await command.ExecuteReaderAsync();
            var list = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                list.Add(row);
            }

            return new OkObjectResult(list);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen templates");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminTemplatesPut")]
    public static async Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/templates/{key}")] HttpRequest req,
        string key,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTemplatesPut");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        if (string.IsNullOrWhiteSpace(key))
            return new BadRequestObjectResult(new { error = "Template key ontbreekt" });

        try
        {
            using var bodyReader = new StreamReader(req.Body);
            var bodyText = await bodyReader.ReadToEndAsync();
            var dto = JsonConvert.DeserializeObject<TemplateRequest>(bodyText);
            if (dto == null || dto.Onderwerp == null || dto.BodyTemplate == null)
                return new BadRequestObjectResult(new { error = "Onderwerp en BodyTemplate verplicht" });

            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(@"
                INSERT INTO public.emailtemplateinstellingen
                    (templatekey, onderwerp, bodytemplate, actief, clubcode)
                VALUES (@key, @onderwerp, @bodytemplate, @actief, @clubcode)
                ON CONFLICT (templatekey, clubcode) DO UPDATE SET
                    onderwerp = @onderwerp,
                    bodytemplate = @bodytemplate,
                    actief = @actief,
                    mta_modified = NOW()", connection);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("onderwerp", dto.Onderwerp);
            command.Parameters.AddWithValue("bodytemplate", dto.BodyTemplate);
            command.Parameters.AddWithValue("actief", dto.Actief ?? true);
            command.Parameters.AddWithValue("clubcode", clubCode);
            await command.ExecuteNonQueryAsync();

            var gewijzigdDoor = dto.GewijzigdDoor ?? "onbekend";
            await using var auditCmd = new NpgsqlCommand(@"
                INSERT INTO public.appsettingsaudit
                    (gewijzigddoor, veld, oudewaarde, nieuwewaarde, clubcode)
                VALUES (@gewijzigddoor, @veld, NULL, @nieuwewaarde, @clubcode)", connection);
            auditCmd.Parameters.AddWithValue("gewijzigddoor", gewijzigdDoor);
            auditCmd.Parameters.AddWithValue("veld", $"template:{key}");
            auditCmd.Parameters.AddWithValue("nieuwewaarde", dto.Onderwerp);
            auditCmd.Parameters.AddWithValue("clubcode", clubCode);
            await auditCmd.ExecuteNonQueryAsync();

            return new OkObjectResult(new { templateKey = key, status = "opgeslagen" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij opslaan template {Key}", key);
            return new ObjectResult(new { error = "Opslaan mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminTemplatesReset")]
    public static async Task<IActionResult> Reset(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/templates/{key}/reset")] HttpRequest req,
        string key,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTemplatesReset");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        if (string.IsNullOrWhiteSpace(key))
            return new BadRequestObjectResult(new { error = "Template key ontbreekt" });

        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(@"
                DELETE FROM public.emailtemplateinstellingen
                WHERE templatekey = @key AND clubcode = @clubcode", connection);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("clubcode", clubCode);
            var rows = await command.ExecuteNonQueryAsync();

            return new OkObjectResult(new { templateKey = key, verwijderd = rows, status = "hardcoded default actief" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij reset template {Key}", key);
            return new ObjectResult(new { error = "Reset mislukt" }) { StatusCode = 500 };
        }
    }

    public class TemplateRequest
    {
        public string? Onderwerp { get; set; }
        public string? BodyTemplate { get; set; }
        public bool? Actief { get; set; }
        public string? GewijzigdDoor { get; set; }
    }
}
