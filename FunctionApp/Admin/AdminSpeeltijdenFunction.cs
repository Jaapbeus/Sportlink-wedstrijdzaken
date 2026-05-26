using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor dbo.Speeltijden (#291).
/// WedstrijdTotaal = speeltijd + rust — de GUI toont dit expliciet.
/// </summary>
public static class AdminSpeeltijdenFunction
{
    [Function("AdminSpeeltijdenGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/speeltijden")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSpeeltijdenGet");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                SELECT [Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust]
                FROM [dbo].[Speeltijden]
                WHERE [ClubCode] = @ClubCode
                ORDER BY
                    CASE WHEN [Leeftijd] LIKE 'JO%'
                         THEN TRY_CAST(SUBSTRING([Leeftijd], 3, 10) AS INT)
                         WHEN [Leeftijd] LIKE 'MO%'
                         THEN 1000 + TRY_CAST(SUBSTRING([Leeftijd], 3, 10) AS INT)
                         WHEN [Leeftijd] LIKE 'G%'
                         THEN 2000
                         WHEN [Leeftijd] = 'VR'
                         THEN 3000
                         ELSE 4000 + TRY_CAST([Leeftijd] AS INT)
                    END
            ", connection);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            using var reader = await command.ExecuteReaderAsync();
            var list = new List<object>();
            while (await reader.ReadAsync())
            {
                list.Add(new
                {
                    Leeftijd = reader.GetString(0),
                    Veldafmeting = reader.GetDecimal(1),
                    WedstrijdTotaal = reader.GetInt32(2),
                    WedstrijdHelft = reader.GetInt32(3),
                    WedstrijdRust = reader.GetInt32(4)
                });
            }
            return new OkObjectResult(list);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen speeltijden");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminSpeeltijdenPost")]
    public static async Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/speeltijden")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSpeeltijdenPost");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

            using var bodyReader = new StreamReader(req.Body);
            var body = await bodyReader.ReadToEndAsync();
            var item = JsonConvert.DeserializeObject<SpeeltijdDto>(body);
            if (item == null || string.IsNullOrWhiteSpace(item.Leeftijd))
                return new BadRequestObjectResult(new { error = "Leeftijd is vereist" });
            if (item.WedstrijdTotaal <= 0)
                return new BadRequestObjectResult(new { error = "WedstrijdTotaal moet groter zijn dan 0" });

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                INSERT INTO [dbo].[Speeltijden] ([Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust], [ClubCode])
                VALUES (@Leeftijd, @Veldafmeting, @WedstrijdTotaal, @WedstrijdHelft, @WedstrijdRust, @ClubCode)
            ", connection);
            command.Parameters.AddWithValue("@Leeftijd", item.Leeftijd.Trim());
            command.Parameters.AddWithValue("@Veldafmeting", item.Veldafmeting);
            command.Parameters.AddWithValue("@WedstrijdTotaal", item.WedstrijdTotaal);
            command.Parameters.AddWithValue("@WedstrijdHelft", item.WedstrijdHelft);
            command.Parameters.AddWithValue("@WedstrijdRust", item.WedstrijdRust);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            await command.ExecuteNonQueryAsync();
            log.LogInformation("Speeltijd toegevoegd: {Leeftijd}", item.Leeftijd);
            return new CreatedResult($"/api/beheer/speeltijden", new { Leeftijd = item.Leeftijd });
        }
        catch (SqlException ex) when (ex.Number == 2627)
        {
            return new ConflictObjectResult(new { error = "Leeftijdscategorie bestaat al" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij toevoegen speeltijd");
            return new ObjectResult(new { error = "Toevoegen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminSpeeltijdenPut")]
    public static async Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/speeltijden/{leeftijd}")] HttpRequest req,
        string leeftijd,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSpeeltijdenPut");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

            using var bodyReader = new StreamReader(req.Body);
            var body = await bodyReader.ReadToEndAsync();
            var item = JsonConvert.DeserializeObject<SpeeltijdDto>(body);
            if (item == null)
                return new BadRequestObjectResult(new { error = "Ongeldige request body" });
            if (item.WedstrijdTotaal <= 0)
                return new BadRequestObjectResult(new { error = "WedstrijdTotaal moet groter zijn dan 0" });

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                UPDATE [dbo].[Speeltijden]
                SET [Veldafmeting] = @Veldafmeting,
                    [WedstrijdTotaal] = @WedstrijdTotaal,
                    [WedstrijdHelft] = @WedstrijdHelft,
                    [WedstrijdRust] = @WedstrijdRust
                WHERE [Leeftijd] = @Leeftijd AND [ClubCode] = @ClubCode
            ", connection);
            command.Parameters.AddWithValue("@Leeftijd", leeftijd);
            command.Parameters.AddWithValue("@Veldafmeting", item.Veldafmeting);
            command.Parameters.AddWithValue("@WedstrijdTotaal", item.WedstrijdTotaal);
            command.Parameters.AddWithValue("@WedstrijdHelft", item.WedstrijdHelft);
            command.Parameters.AddWithValue("@WedstrijdRust", item.WedstrijdRust);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            var rows = await command.ExecuteNonQueryAsync();
            if (rows == 0) return new NotFoundObjectResult(new { error = "Leeftijdscategorie niet gevonden" });
            log.LogInformation("Speeltijd bijgewerkt: {Leeftijd}", leeftijd);
            return new OkObjectResult(new { updated = leeftijd });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij bijwerken speeltijd {Leeftijd}", leeftijd);
            return new ObjectResult(new { error = "Bijwerken mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminSpeeltijdenDelete")]
    public static async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/speeltijden/{leeftijd}")] HttpRequest req,
        string leeftijd,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSpeeltijdenDelete");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(
                "DELETE FROM [dbo].[Speeltijden] WHERE [Leeftijd] = @Leeftijd AND [ClubCode] = @ClubCode",
                connection);
            command.Parameters.AddWithValue("@Leeftijd", leeftijd);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            var rows = await command.ExecuteNonQueryAsync();
            if (rows == 0) return new NotFoundObjectResult(new { error = "Leeftijdscategorie niet gevonden" });
            log.LogInformation("Speeltijd verwijderd: {Leeftijd}", leeftijd);
            return new OkObjectResult(new { deleted = leeftijd });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij verwijderen speeltijd {Leeftijd}", leeftijd);
            return new ObjectResult(new { error = "Verwijderen mislukt" }) { StatusCode = 500 };
        }
    }

    private record SpeeltijdDto(
        string Leeftijd,
        decimal Veldafmeting,
        int WedstrijdTotaal,
        int WedstrijdHelft,
        int WedstrijdRust);
}
