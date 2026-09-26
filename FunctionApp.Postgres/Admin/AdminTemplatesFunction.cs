using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using FunctionApp.Postgres.Email;
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
/// <b>Cache-invalidatie (#889):</b> <c>Put</c> en <c>Reset</c> roepen nu
/// <see cref="Email.EmailTemplateService.InvalidateCache"/> aan, gelijk aan de SQL Server-tier.
/// Die aanroep ontbrak eerder bewust omdat de service op deze tier nog niet bestond; hij bestaat nu.
/// </para>
/// </summary>
public static class AdminTemplatesFunction
{
    [Function("AdminTemplatesGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/templates")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTemplatesGet"), "templates ophalen",
            async clubCode =>
            {
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
            });

    [Function("AdminTemplatesPut")]
    public static Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/templates/{key}")] HttpRequest req,
        string key,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTemplatesPut"), $"template {key} opslaan",
            async clubCode =>
            {
                if (string.IsNullOrWhiteSpace(key))
                    return new BadRequestObjectResult(new { error = "Template key ontbreekt" });

                using var bodyReader = new StreamReader(req.Body);
                var bodyText = await bodyReader.ReadToEndAsync();
                var dto = JsonConvert.DeserializeObject<TemplateRequest>(bodyText);
                if (dto == null || dto.Onderwerp == null || dto.BodyTemplate == null)
                    return new BadRequestObjectResult(new { error = "Onderwerp en BodyTemplate verplicht" });

                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                // Issue 913/916-precedent: upsert + auditlog-insert in één transactie, zelfde patroon
                // als AdminSettingsFunction.Put — anders kan een fout tussen de twee statements een
                // wél-doorgevoerde templatewijziging zonder auditrij achterlaten.
                await using var transaction = await connection.BeginTransactionAsync();
                try
                {
                    await using var command = new NpgsqlCommand(@"
                        INSERT INTO public.emailtemplateinstellingen
                            (templatekey, onderwerp, bodytemplate, actief, clubcode)
                        VALUES (@key, @onderwerp, @bodytemplate, @actief, @clubcode)
                        ON CONFLICT (templatekey, clubcode) DO UPDATE SET
                            onderwerp = @onderwerp,
                            bodytemplate = @bodytemplate,
                            actief = @actief,
                            mta_modified = NOW()", connection, transaction);
                    command.Parameters.AddWithValue("key", key);
                    command.Parameters.AddWithValue("onderwerp", dto.Onderwerp);
                    command.Parameters.AddWithValue("bodytemplate", dto.BodyTemplate);
                    command.Parameters.AddWithValue("actief", dto.Actief ?? true);
                    command.Parameters.AddWithValue("clubcode", clubCode);
                    await command.ExecuteNonQueryAsync();

                    // #1003: audit-actor komt uitsluitend uit gevalideerde Easy Auth-claims, nooit uit
                    // de request-body — zelfde fix als AdminSettingsFunction.Put.
                    var gewijzigdDoor = EasyAuthHelper.GetAuditActor(req);
                    await using var auditCmd = new NpgsqlCommand(@"
                        INSERT INTO public.appsettingsaudit
                            (gewijzigddoor, veld, oudewaarde, nieuwewaarde, clubcode)
                        VALUES (@gewijzigddoor, @veld, NULL, @nieuwewaarde, @clubcode)", connection, transaction);
                    auditCmd.Parameters.AddWithValue("gewijzigddoor", gewijzigdDoor);
                    auditCmd.Parameters.AddWithValue("veld", $"template:{key}");
                    auditCmd.Parameters.AddWithValue("nieuwewaarde", dto.Onderwerp);
                    auditCmd.Parameters.AddWithValue("clubcode", clubCode);
                    await auditCmd.ExecuteNonQueryAsync();

                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }

                // Cache invalideren zodat het nieuwe sjabloon direct gebruikt wordt (#889).
                EmailTemplateService.InvalidateCache();

                return new OkObjectResult(new { templateKey = key, status = "opgeslagen" });
            });

    [Function("AdminTemplatesReset")]
    public static Task<IActionResult> Reset(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/templates/{key}/reset")] HttpRequest req,
        string key,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTemplatesReset"), $"template {key} resetten",
            async clubCode =>
            {
                if (string.IsNullOrWhiteSpace(key))
                    return new BadRequestObjectResult(new { error = "Template key ontbreekt" });

                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(@"
                    DELETE FROM public.emailtemplateinstellingen
                    WHERE templatekey = @key AND clubcode = @clubcode", connection);
                command.Parameters.AddWithValue("key", key);
                command.Parameters.AddWithValue("clubcode", clubCode);
                var rows = await command.ExecuteNonQueryAsync();

                EmailTemplateService.InvalidateCache();
                return new OkObjectResult(new { templateKey = key, verwijderd = rows, status = "hardcoded default actief" });
            });

    public class TemplateRequest
    {
        public string? Onderwerp { get; set; }
        public string? BodyTemplate { get; set; }
        public bool? Actief { get; set; }
        // #1003: GewijzigdDoor bewust verwijderd — zie AdminSettingsFunction.UpdateSettingsRequest.
    }
}
