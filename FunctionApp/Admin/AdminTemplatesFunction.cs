using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SportlinkFunction.Email;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor EmailTemplateInstellingen. v2 — #90.
///
/// GET  /api/beheer/templates            → alle actieve templates
/// PUT  /api/beheer/templates/{key}      → upsert template (Onderwerp + BodyTemplate)
/// POST /api/beheer/templates/{key}/reset → verwijder rij; hardcoded default treedt weer in
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
                using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                using var command = new SqlCommand(@"
                    SELECT [Id], [TemplateKey], [Onderwerp], [BodyTemplate], [Actief], [ClubCode],
                           [mta_inserted], [mta_modified]
                    FROM [dbo].[EmailTemplateInstellingen]
                    WHERE [ClubCode] = @ClubCode
                    ORDER BY [TemplateKey]", connection);
                command.Parameters.AddWithValue("@ClubCode", clubCode);

                using var reader = await command.ExecuteReaderAsync();
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

                using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                // Issue 916: upsert + auditlog-insert in één transactie, zelfde patroon als
                // AdminSettingsFunction.Put — anders kan een fout tussen de twee statements een
                // wél-doorgevoerde templatewijziging zonder auditrij achterlaten.
                using var transaction = await connection.BeginTransactionAsync();
                try
                {
                    using var command = new SqlCommand(@"
                        MERGE [dbo].[EmailTemplateInstellingen] AS T
                        USING (SELECT @Key AS [TemplateKey], @ClubCode AS [ClubCode]) AS S
                          ON  T.[TemplateKey] = S.[TemplateKey] AND T.[ClubCode] = S.[ClubCode]
                        WHEN MATCHED THEN UPDATE SET
                            [Onderwerp] = @Onderwerp,
                            [BodyTemplate] = @BodyTemplate,
                            [Actief] = @Actief,
                            [mta_modified] = GETUTCDATE()
                        WHEN NOT MATCHED THEN INSERT
                            ([TemplateKey], [Onderwerp], [BodyTemplate], [Actief], [ClubCode])
                            VALUES (@Key, @Onderwerp, @BodyTemplate, @Actief, @ClubCode);",
                        connection, (SqlTransaction)transaction);
                    command.Parameters.AddWithValue("@Key", key);
                    command.Parameters.AddWithValue("@Onderwerp", dto.Onderwerp);
                    command.Parameters.AddWithValue("@BodyTemplate", dto.BodyTemplate);
                    command.Parameters.AddWithValue("@Actief", dto.Actief ?? true);
                    command.Parameters.AddWithValue("@ClubCode", clubCode);
                    await command.ExecuteNonQueryAsync();

                    // #1003: audit-actor komt uitsluitend uit gevalideerde Easy Auth-claims, nooit uit
                    // de request-body — zelfde fix als AdminSettingsFunction.Put.
                    var gewijzigdDoor = EasyAuthHelper.GetAuditActor(req);
                    using var auditCmd = new SqlCommand(@"
                        INSERT INTO [dbo].[AppSettingsAudit]
                            ([GewijzigdDoor], [Veld], [OudeWaarde], [NieuweWaarde], [ClubCode])
                        VALUES (@GewijzigdDoor, @Veld, NULL, @NieuweWaarde, @ClubCode)",
                        connection, (SqlTransaction)transaction);
                    auditCmd.Parameters.AddWithValue("@GewijzigdDoor", gewijzigdDoor);
                    auditCmd.Parameters.AddWithValue("@Veld", $"template:{key}");
                    auditCmd.Parameters.AddWithValue("@NieuweWaarde", dto.Onderwerp);
                    auditCmd.Parameters.AddWithValue("@ClubCode", clubCode);
                    await auditCmd.ExecuteNonQueryAsync();

                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }

                // Cache invalideren zodat de nieuwe template direct gebruikt wordt
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

                using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                using var command = new SqlCommand(@"
                    DELETE FROM [dbo].[EmailTemplateInstellingen]
                    WHERE [TemplateKey] = @Key AND [ClubCode] = @ClubCode", connection);
                command.Parameters.AddWithValue("@Key", key);
                command.Parameters.AddWithValue("@ClubCode", clubCode);
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
