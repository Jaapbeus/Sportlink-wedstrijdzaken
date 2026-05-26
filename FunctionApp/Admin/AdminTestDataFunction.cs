using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace SportlinkFunction.Admin;

public static class AdminTestDataFunction
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // GET /api/beheer/testdata/wedstrijden
    [Function("TestDataWedstrijdenGet")]
    public static async Task<IActionResult> GetWedstrijden(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/testdata/wedstrijden")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("TestDataWedstrijdenGet");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                SELECT [bk_matches], [datum], [aanvangstijd],
                       [thuisteam], [uitteam], [veld], [competitiesoort]
                FROM   [his].[matches]
                WHERE  [ClubCode] = 'ALLSTARS'
                ORDER  BY [datum], [aanvangstijd], [thuisteam]",
                connection);
            using var reader = await command.ExecuteReaderAsync();
            var list = new List<object>();
            while (await reader.ReadAsync())
                list.Add(new
                {
                    BkMatches   = reader.GetString(0),
                    Datum       = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Aanvangstijd = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ThuisTeam   = reader.IsDBNull(3) ? null : reader.GetString(3),
                    UitTeam     = reader.IsDBNull(4) ? null : reader.GetString(4),
                    VeldNaam    = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Soort       = reader.IsDBNull(6) ? null : reader.GetString(6),
                });
            return new OkObjectResult(list);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen ALLSTARS wedstrijden");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    // GET /api/beheer/testdata/teams — teams uit avg.Teambegeleiding voor ALLSTARS
    [Function("TestDataTeamsGet")]
    public static async Task<IActionResult> GetTeams(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/testdata/teams")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("TestDataTeamsGet");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                SELECT DISTINCT [Team]
                FROM   [avg].[Teambegeleiding]
                WHERE  [Team] IS NOT NULL AND [ClubCode] = 'ALLSTARS'
                ORDER  BY [Team]",
                connection);
            using var reader = await command.ExecuteReaderAsync();
            var teams = new List<string>();
            while (await reader.ReadAsync())
                teams.Add(reader.GetString(0));
            return new OkObjectResult(teams);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen teams voor testdata");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    // POST /api/beheer/testdata/wedstrijden — upsert één wedstrijd
    [Function("TestDataWedstrijdUpsert")]
    public static async Task<IActionResult> UpsertWedstrijd(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/testdata/wedstrijden")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("TestDataWedstrijdUpsert");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var dto = JsonSerializer.Deserialize<AllstarsWedstrijdInput>(body, _json);
            if (dto == null || string.IsNullOrWhiteSpace(dto.BkMatches))
                return new BadRequestObjectResult(new { error = "BkMatches verplicht" });

            // Afleiden van wedstrijddatum en kaledatum vanuit datum-string ("2026-05-30")
            var wedstrijddatum = (!string.IsNullOrWhiteSpace(dto.Datum) && !string.IsNullOrWhiteSpace(dto.Aanvangstijd))
                ? $"{dto.Datum}T{dto.Aanvangstijd}:00"
                : dto.Datum;
            var kaledatum = !string.IsNullOrWhiteSpace(dto.Datum)
                ? $"{dto.Datum} 00:00:00.00"
                : null;

            await SystemUtilities.WaitForDatabaseAsync(log);
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                MERGE [his].[matches] AS target
                USING (SELECT @BkMatches AS bk) AS source ON target.bk_matches = source.bk
                WHEN MATCHED THEN UPDATE SET
                    [datum]           = @Datum,
                    [wedstrijddatum]  = @Wedstrijddatum,
                    [kaledatum]       = @Kaledatum,
                    [aanvangstijd]    = @Aanvangstijd,
                    [thuisteam]       = @ThuisTeam,
                    [teamnaam]        = @ThuisTeam,
                    [uitteam]         = @UitTeam,
                    [veld]            = @VeldNaam,
                    [competitiesoort] = @Soort,
                    [mta_modified]    = GETUTCDATE()
                WHEN NOT MATCHED THEN INSERT
                    ([bk_matches], [datum], [wedstrijddatum], [kaledatum], [aanvangstijd],
                     [thuisteam], [teamnaam], [uitteam], [veld], [competitiesoort],
                     [ClubCode], [mta_inserted], [mta_modified])
                VALUES
                    (@BkMatches, @Datum, @Wedstrijddatum, @Kaledatum, @Aanvangstijd,
                     @ThuisTeam, @ThuisTeam, @UitTeam, @VeldNaam, @Soort,
                     'ALLSTARS', GETUTCDATE(), GETUTCDATE());",
                connection);

            command.Parameters.AddWithValue("@BkMatches",    dto.BkMatches);
            command.Parameters.AddWithValue("@Datum",        (object?)dto.Datum         ?? DBNull.Value);
            command.Parameters.AddWithValue("@Wedstrijddatum",(object?)wedstrijddatum   ?? DBNull.Value);
            command.Parameters.AddWithValue("@Kaledatum",    (object?)kaledatum         ?? DBNull.Value);
            command.Parameters.AddWithValue("@Aanvangstijd", (object?)dto.Aanvangstijd  ?? DBNull.Value);
            command.Parameters.AddWithValue("@ThuisTeam",    (object?)dto.ThuisTeam     ?? DBNull.Value);
            command.Parameters.AddWithValue("@UitTeam",      (object?)dto.UitTeam       ?? DBNull.Value);
            command.Parameters.AddWithValue("@VeldNaam",     (object?)dto.VeldNaam      ?? DBNull.Value);
            command.Parameters.AddWithValue("@Soort",        (object?)dto.Soort         ?? DBNull.Value);

            await command.ExecuteNonQueryAsync();
            return new OkObjectResult(new { ok = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij upsert ALLSTARS wedstrijd");
            return new ObjectResult(new { error = "Opslaan mislukt" }) { StatusCode = 500 };
        }
    }

    // DELETE /api/beheer/testdata/wedstrijden/{bk} — verwijder één wedstrijd
    [Function("TestDataWedstrijdDeleteEen")]
    public static async Task<IActionResult> DeleteEen(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/testdata/wedstrijden/{bk}")] HttpRequest req,
        string bk,
        FunctionContext context)
    {
        var log = context.GetLogger("TestDataWedstrijdDeleteEen");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                DELETE FROM [his].[matches]
                WHERE  [bk_matches] = @Bk AND [ClubCode] = 'ALLSTARS'",
                connection);
            command.Parameters.AddWithValue("@Bk", bk);
            await command.ExecuteNonQueryAsync();
            return new OkObjectResult(new { ok = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij verwijderen ALLSTARS wedstrijd {Bk}", bk);
            return new ObjectResult(new { error = "Verwijderen mislukt" }) { StatusCode = 500 };
        }
    }

    // DELETE /api/beheer/testdata/wedstrijden?van=YYYY-MM-DD&tot=YYYY-MM-DD
    // Verwijdert ALLSTARS-wedstrijden voor het opgegeven datumbereik (beide params optioneel).
    [Function("TestDataWedstrijdenDeleteAlle")]
    public static async Task<IActionResult> DeleteAlle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/testdata/wedstrijden")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("TestDataWedstrijdenDeleteAlle");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            var vanStr = req.Query.ContainsKey("van") ? req.Query["van"].ToString() : null;
            var totStr = req.Query.ContainsKey("tot") ? req.Query["tot"].ToString() : null;

            var sql = "DELETE FROM [his].[matches] WHERE [ClubCode] = 'ALLSTARS'";
            if (!string.IsNullOrEmpty(vanStr)) sql += " AND [datum] >= @Van";
            if (!string.IsNullOrEmpty(totStr)) sql += " AND [datum] <= @Tot";

            await SystemUtilities.WaitForDatabaseAsync(log);
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(sql, connection);
            if (!string.IsNullOrEmpty(vanStr)) command.Parameters.AddWithValue("@Van", vanStr);
            if (!string.IsNullOrEmpty(totStr)) command.Parameters.AddWithValue("@Tot", totStr);
            await command.ExecuteNonQueryAsync();
            return new OkObjectResult(new { ok = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij verwijderen ALLSTARS wedstrijden");
            return new ObjectResult(new { error = "Verwijderen mislukt" }) { StatusCode = 500 };
        }
    }

    private sealed class AllstarsWedstrijdInput
    {
        public string  BkMatches    { get; set; } = "";
        public string? Datum        { get; set; }
        public string? Aanvangstijd { get; set; }
        public string? ThuisTeam    { get; set; }
        public string? UitTeam      { get; set; }
        public string? VeldNaam     { get; set; }
        public string? Soort        { get; set; }
    }
}
