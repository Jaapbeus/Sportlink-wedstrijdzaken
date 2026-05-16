using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor email-verwerkingslog. v2 — #93.
///
/// GET /api/beheer/email-log?vanaf=YYYY-MM-DD&amp;tot=YYYY-MM-DD&amp;status=X&amp;limit=50
///
/// AVG: NOOIT EmailBody of AntwoordEmail teruggeven; alleen metadata.
/// </summary>
public static class AdminEmailLogFunction
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    [Function("AdminEmailLogGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "beheer/email-log")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminEmailLogGet");
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            DateTime? vanaf = null, tot = null;
            if (DateTime.TryParse(req.Query["vanaf"].ToString(), out var vd)) vanaf = vd.Date;
            if (DateTime.TryParse(req.Query["tot"].ToString(), out var td)) tot = td.Date.AddDays(1);
            var statusFilter = req.Query["status"].ToString();
            int limit = DefaultLimit;
            if (int.TryParse(req.Query["limit"].ToString(), out var l))
                limit = Math.Min(MaxLimit, Math.Max(1, l));

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            // AVG: alleen metadata, GEEN [EmailBody] en GEEN [AntwoordEmail]
            var sql = @"SELECT TOP (@Limit) [Id], [MessageId], [ConversationId], [Afzender], [Onderwerp],
                               [OntvangstDatum], [VerzoekType], [Status], [VerstuurdNaar],
                               [FoutMelding], [mta_inserted], [mta_modified]
                        FROM [planner].[EmailVerwerking]
                        WHERE 1 = 1";
            if (vanaf.HasValue) sql += " AND [OntvangstDatum] >= @Vanaf";
            if (tot.HasValue) sql += " AND [OntvangstDatum] < @Tot";
            if (!string.IsNullOrWhiteSpace(statusFilter)) sql += " AND [Status] = @Status";
            sql += " ORDER BY [OntvangstDatum] DESC";

            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Limit", limit);
            if (vanaf.HasValue) command.Parameters.AddWithValue("@Vanaf", vanaf.Value);
            if (tot.HasValue) command.Parameters.AddWithValue("@Tot", tot.Value);
            if (!string.IsNullOrWhiteSpace(statusFilter))
                command.Parameters.AddWithValue("@Status", statusFilter);

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

            return new OkObjectResult(new
            {
                count = list.Count,
                limit,
                items = list
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen email-log");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }
}
