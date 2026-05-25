using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor het beheren van classificatie-leermomenten. v2 — #323.
///
/// GET /api/beheer/leermomenten?status=pending|validated|rejected&amp;limit=50
/// GET /api/beheer/leermomenten/stats
/// PUT /api/beheer/leermomenten/{id}/valideer  body: { "actie": "valideer" | "afwijzen" }
/// </summary>
public static class AdminLeermomentenFunction
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    [Function("AdminLeermomentenGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/leermomenten")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminLeermomentenGet");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

            var statusFilter = req.Query["status"].ToString();
            int limit = DefaultLimit;
            if (int.TryParse(req.Query["limit"].ToString(), out var l))
                limit = Math.Min(MaxLimit, Math.Max(1, l));

            string whereClause = statusFilter switch
            {
                "pending"   => "AND [IsGevalideerd] = 0 AND [IsAfgewezen] = 0",
                "validated" => "AND [IsGevalideerd] = 1",
                "rejected"  => "AND [IsAfgewezen] = 1",
                _           => ""
            };

            var sql = $@"
                SELECT TOP (@Limit)
                    cc.[Id], cc.[OrigineleVerwerkingId], cc.[CorrectionVerwerkingId],
                    cc.[OrigineelVerzoekType], cc.[AfgeleidJuistType],
                    cc.[OrigineleSamenvatting], cc.[CorrectieSamenvatting],
                    cc.[IsGevalideerd], cc.[IsAfgewezen],
                    cc.[mta_inserted], cc.[mta_modified]
                FROM [planner].[ClassificatieCorrectie] cc
                WHERE cc.[ClubCode] = @ClubCode {whereClause}
                ORDER BY cc.[mta_inserted] DESC";

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Limit", limit);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            using var reader = await command.ExecuteReaderAsync();
            var list = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[name] = raw is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : raw;
                }
                list.Add(row);
            }

            return new OkObjectResult(new { count = list.Count, limit, items = list });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen leermomenten");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminLeermomentenStats")]
    public static async Task<IActionResult> GetStats(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/leermomenten/stats")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminLeermomentenStats");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                SELECT
                    SUM(CASE WHEN [IsGevalideerd] = 0 AND [IsAfgewezen] = 0 THEN 1 ELSE 0 END) AS Pending,
                    SUM(CASE WHEN [IsGevalideerd] = 1 THEN 1 ELSE 0 END) AS Validated,
                    SUM(CASE WHEN [IsAfgewezen] = 1 THEN 1 ELSE 0 END) AS Rejected
                FROM [planner].[ClassificatieCorrectie]
                WHERE [ClubCode] = @ClubCode", connection);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return new OkObjectResult(new { pending = 0, validated = 0, rejected = 0 });

            return new OkObjectResult(new
            {
                pending   = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                validated = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                rejected  = reader.IsDBNull(2) ? 0 : reader.GetInt32(2)
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen leermomenten-stats");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminLeermomentenValideer")]
    public static async Task<IActionResult> Valideer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/leermomenten/{id}/valideer")] HttpRequest req,
        int id,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminLeermomentenValideer");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

            string body;
            using (var sr = new System.IO.StreamReader(req.Body))
                body = await sr.ReadToEndAsync();

            string? actie = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("actie", out var a))
                    actie = a.GetString();
            }
            catch { }

            if (actie != "valideer" && actie != "afwijzen")
                return new BadRequestObjectResult(new { error = "Ongeldige actie. Gebruik 'valideer' of 'afwijzen'." });

            bool isGevalideerd = actie == "valideer";
            bool isAfgewezen   = actie == "afwijzen";

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                UPDATE [planner].[ClassificatieCorrectie]
                SET [IsGevalideerd] = @IsGevalideerd, [IsAfgewezen] = @IsAfgewezen,
                    [mta_modified] = GETUTCDATE()
                WHERE [Id] = @Id AND [ClubCode] = @ClubCode", connection);
            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@IsGevalideerd", isGevalideerd);
            command.Parameters.AddWithValue("@IsAfgewezen", isAfgewezen);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            var rows = await command.ExecuteNonQueryAsync();
            if (rows == 0)
                return new NotFoundObjectResult(new { error = $"Leermoment {id} niet gevonden." });

            log.LogInformation("Leermoment {Id} {Actie} door admin", id, actie);
            return new OkObjectResult(new { id, actie });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij valideren leermoment {Id}", id);
            return new ObjectResult(new { error = "Actie mislukt" }) { StatusCode = 500 };
        }
    }
}
