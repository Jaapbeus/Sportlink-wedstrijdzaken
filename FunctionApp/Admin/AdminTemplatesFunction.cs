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
/// GET  /api/admin/templates            → alle actieve templates
/// PUT  /api/admin/templates/{key}      → upsert template (Onderwerp + BodyTemplate)
/// POST /api/admin/templates/{key}/reset → verwijder rij; hardcoded default treedt weer in
/// </summary>
public static class AdminTemplatesFunction
{
    [Function("AdminTemplatesGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "admin/templates")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTemplatesGet");
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode") ?? "VRC";

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
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen templates");
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    [Function("AdminTemplatesPut")]
    public static async Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "admin/templates/{key}")] HttpRequest req,
        string key,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTemplatesPut");
        if (string.IsNullOrWhiteSpace(key))
            return new BadRequestObjectResult(new { error = "Template key ontbreekt" });

        try
        {
            using var bodyReader = new StreamReader(req.Body);
            var bodyText = await bodyReader.ReadToEndAsync();
            var dto = JsonConvert.DeserializeObject<TemplateRequest>(bodyText);
            if (dto == null || dto.Onderwerp == null || dto.BodyTemplate == null)
                return new BadRequestObjectResult(new { error = "Onderwerp en BodyTemplate verplicht" });

            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode") ?? "VRC";

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                MERGE [dbo].[EmailTemplateInstellingen] AS T
                USING (SELECT @Key AS [TemplateKey], @ClubCode AS [ClubCode]) AS S
                  ON  T.[TemplateKey] = S.[TemplateKey] AND T.[ClubCode] = S.[ClubCode]
                WHEN MATCHED THEN UPDATE SET
                    [Onderwerp] = @Onderwerp,
                    [BodyTemplate] = @BodyTemplate,
                    [Actief] = @Actief,
                    [mta_modified] = GETDATE()
                WHEN NOT MATCHED THEN INSERT
                    ([TemplateKey], [Onderwerp], [BodyTemplate], [Actief], [ClubCode])
                    VALUES (@Key, @Onderwerp, @BodyTemplate, @Actief, @ClubCode);", connection);
            command.Parameters.AddWithValue("@Key", key);
            command.Parameters.AddWithValue("@Onderwerp", dto.Onderwerp);
            command.Parameters.AddWithValue("@BodyTemplate", dto.BodyTemplate);
            command.Parameters.AddWithValue("@Actief", dto.Actief ?? true);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            await command.ExecuteNonQueryAsync();

            // Audit-log
            var gewijzigdDoor = dto.GewijzigdDoor ?? "onbekend";
            using var auditCmd = new SqlCommand(@"
                INSERT INTO [dbo].[AppSettingsAudit]
                    ([GewijzigdDoor], [Veld], [OudeWaarde], [NieuweWaarde], [ClubCode])
                VALUES (@GewijzigdDoor, @Veld, NULL, @NieuweWaarde, @ClubCode)", connection);
            auditCmd.Parameters.AddWithValue("@GewijzigdDoor", gewijzigdDoor);
            auditCmd.Parameters.AddWithValue("@Veld", $"template:{key}");
            auditCmd.Parameters.AddWithValue("@NieuweWaarde", dto.Onderwerp);
            auditCmd.Parameters.AddWithValue("@ClubCode", clubCode);
            await auditCmd.ExecuteNonQueryAsync();

            // Cache invalideren zodat de nieuwe template direct gebruikt wordt
            EmailTemplateService.InvalidateCache();

            return new OkObjectResult(new { templateKey = key, status = "opgeslagen" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij opslaan template {Key}", key);
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    [Function("AdminTemplatesReset")]
    public static async Task<IActionResult> Reset(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "admin/templates/{key}/reset")] HttpRequest req,
        string key,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTemplatesReset");
        if (string.IsNullOrWhiteSpace(key))
            return new BadRequestObjectResult(new { error = "Template key ontbreekt" });

        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode") ?? "VRC";

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
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij reset template {Key}", key);
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
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
