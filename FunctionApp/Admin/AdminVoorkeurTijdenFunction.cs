using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor TeamVoorkeurTijden. v2 — #91 / #62.
/// </summary>
public static class AdminVoorkeurTijdenFunction
{
    [Function("AdminVoorkeurTijdenGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/voorkeurstijden")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVoorkeurTijdenGet");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            string? team = req.Query["team"].ToString();
            if (string.IsNullOrWhiteSpace(team)) team = null;

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            var sql = @"SELECT [Id], [TeamNaam], [DagVanWeek], CONVERT(VARCHAR(5), [VoorkeurTijd]) AS [VoorkeurTijd],
                               [Prioriteit], [Actief], [ClubCode], [mta_inserted], [mta_modified]
                        FROM [dbo].[TeamVoorkeurTijden]
                        WHERE [ClubCode] = @ClubCode";
            if (team != null) sql += " AND [TeamNaam] = @Team";
            sql += " ORDER BY [TeamNaam], [DagVanWeek], [VoorkeurTijd]";

            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            if (team != null) command.Parameters.AddWithValue("@Team", team);

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
            log.LogError(ex, "Fout bij ophalen voorkeurstijden");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminVoorkeurTijdenPost")]
    public static async Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/voorkeurstijden")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVoorkeurTijdenPost");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            using var bodyReader = new StreamReader(req.Body);
            var dto = JsonConvert.DeserializeObject<VoorkeurTijdRequest>(await bodyReader.ReadToEndAsync());
            var validatie = Valideer(dto);
            if (validatie != null) return validatie;

            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                INSERT INTO [dbo].[TeamVoorkeurTijden]
                    ([TeamNaam], [DagVanWeek], [VoorkeurTijd], [Prioriteit], [Actief], [ClubCode])
                VALUES (@TeamNaam, @DagVanWeek, @VoorkeurTijd, @Prioriteit, @Actief, @ClubCode);
                SELECT CAST(SCOPE_IDENTITY() AS INT);", connection);
            command.Parameters.AddWithValue("@TeamNaam", dto!.TeamNaam!);
            command.Parameters.AddWithValue("@DagVanWeek", dto.DagVanWeek!);
            command.Parameters.AddWithValue("@VoorkeurTijd", TimeSpan.Parse(dto.VoorkeurTijd!));
            command.Parameters.AddWithValue("@Prioriteit", dto.Prioriteit ?? 5);
            command.Parameters.AddWithValue("@Actief", dto.Actief ?? true);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            var newId = (int)(await command.ExecuteScalarAsync())!;
            return new OkObjectResult(new { id = newId, status = "aangemaakt" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij aanmaken voorkeurstijd");
            return new ObjectResult(new { error = "Aanmaken mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminVoorkeurTijdenPut")]
    public static async Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/voorkeurstijden/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVoorkeurTijdenPut");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            using var bodyReader = new StreamReader(req.Body);
            var dto = JsonConvert.DeserializeObject<VoorkeurTijdRequest>(await bodyReader.ReadToEndAsync());
            var validatie = Valideer(dto);
            if (validatie != null) return validatie;

            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                UPDATE [dbo].[TeamVoorkeurTijden]
                SET [TeamNaam] = @TeamNaam, [DagVanWeek] = @DagVanWeek, [VoorkeurTijd] = @VoorkeurTijd,
                    [Prioriteit] = @Prioriteit, [Actief] = @Actief, [mta_modified] = GETUTCDATE()
                WHERE [Id] = @Id AND [ClubCode] = @ClubCode", connection);
            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            command.Parameters.AddWithValue("@TeamNaam", dto!.TeamNaam!);
            command.Parameters.AddWithValue("@DagVanWeek", dto.DagVanWeek!);
            command.Parameters.AddWithValue("@VoorkeurTijd", TimeSpan.Parse(dto.VoorkeurTijd!));
            command.Parameters.AddWithValue("@Prioriteit", dto.Prioriteit ?? 5);
            command.Parameters.AddWithValue("@Actief", dto.Actief ?? true);
            var rows = await command.ExecuteNonQueryAsync();

            if (rows == 0)
                return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });

            return new OkObjectResult(new { id, status = "bijgewerkt" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij bijwerken voorkeurstijd {Id}", id);
            return new ObjectResult(new { error = "Bijwerken mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminVoorkeurTijdenDelete")]
    public static async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/voorkeurstijden/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVoorkeurTijdenDelete");
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
                UPDATE [dbo].[TeamVoorkeurTijden]
                SET [Actief] = 0, [mta_modified] = GETUTCDATE()
                WHERE [Id] = @Id AND [ClubCode] = @ClubCode", connection);
            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            var rows = await command.ExecuteNonQueryAsync();

            if (rows == 0)
                return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });

            return new OkObjectResult(new { id, status = "soft-deleted" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij verwijderen voorkeurstijd {Id}", id);
            return new ObjectResult(new { error = "Verwijderen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminTeamRegelsGet")]
    public static async Task<IActionResult> GetTeamRegels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/teamregels")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTeamRegelsGet");
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

            // Beheer-endpoint: altijd alle regels (ook inactief), zodat ze heractiveerbaar zijn
            using var command = new SqlCommand(@"
                SELECT [Id], [TeamNaam], [RegelType], [WaardeMinuten], [WaardeVeldNummer],
                       CONVERT(VARCHAR(5), [WaardeTijd]) AS [WaardeTijd],
                       [Prioriteit], [Actief], [Opmerking], [ClubCode]
                FROM [dbo].[TeamRegels]
                WHERE [ClubCode] = @ClubCode
                ORDER BY [TeamNaam], [Prioriteit]", connection);
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
            log.LogError(ex, "Fout bij ophalen teamregels");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminTeamRegelsPost")]
    public static async Task<IActionResult> PostTeamRegel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/teamregels")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTeamRegelsPost");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            using var bodyReader = new StreamReader(req.Body);
            var dto = JsonConvert.DeserializeObject<TeamRegelRequest>(await bodyReader.ReadToEndAsync());
            var validatie = ValideerRegel(dto);
            if (validatie != null) return validatie;

            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                INSERT INTO [dbo].[TeamRegels]
                    ([TeamNaam], [RegelType], [WaardeMinuten], [WaardeVeldNummer], [WaardeTijd],
                     [Prioriteit], [Actief], [Opmerking], [ClubCode])
                VALUES
                    (@TeamNaam, @RegelType, @WaardeMinuten, @WaardeVeldNummer, @WaardeTijd,
                     @Prioriteit, @Actief, @Opmerking, @ClubCode);
                SELECT CAST(SCOPE_IDENTITY() AS INT);", connection);
            command.Parameters.AddWithValue("@TeamNaam", dto!.TeamNaam!);
            command.Parameters.AddWithValue("@RegelType", dto.RegelType!);
            command.Parameters.AddWithValue("@WaardeMinuten", (object?)dto.WaardeMinuten ?? DBNull.Value);
            command.Parameters.AddWithValue("@WaardeVeldNummer", (object?)dto.WaardeVeldNummer ?? DBNull.Value);
            command.Parameters.AddWithValue("@WaardeTijd",
                string.IsNullOrWhiteSpace(dto.WaardeTijd) ? DBNull.Value : TimeSpan.Parse(dto.WaardeTijd));
            command.Parameters.AddWithValue("@Prioriteit", dto.Prioriteit ?? 0);
            command.Parameters.AddWithValue("@Actief", dto.Actief ?? true);
            command.Parameters.AddWithValue("@Opmerking", (object?)dto.Opmerking ?? DBNull.Value);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            var newId = (int)(await command.ExecuteScalarAsync())!;
            return new OkObjectResult(new { id = newId, status = "aangemaakt" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij aanmaken teamregel");
            return new ObjectResult(new { error = "Aanmaken mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminTeamRegelsPut")]
    public static async Task<IActionResult> PutTeamRegel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/teamregels/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTeamRegelsPut");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            using var bodyReader = new StreamReader(req.Body);
            var dto = JsonConvert.DeserializeObject<TeamRegelRequest>(await bodyReader.ReadToEndAsync());
            var validatie = ValideerRegel(dto);
            if (validatie != null) return validatie;

            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                UPDATE [dbo].[TeamRegels]
                SET [TeamNaam] = @TeamNaam, [RegelType] = @RegelType,
                    [WaardeMinuten] = @WaardeMinuten, [WaardeVeldNummer] = @WaardeVeldNummer,
                    [WaardeTijd] = @WaardeTijd, [Prioriteit] = @Prioriteit,
                    [Actief] = @Actief, [Opmerking] = @Opmerking
                WHERE [Id] = @Id AND [ClubCode] = @ClubCode", connection);
            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            command.Parameters.AddWithValue("@TeamNaam", dto!.TeamNaam!);
            command.Parameters.AddWithValue("@RegelType", dto.RegelType!);
            command.Parameters.AddWithValue("@WaardeMinuten", (object?)dto.WaardeMinuten ?? DBNull.Value);
            command.Parameters.AddWithValue("@WaardeVeldNummer", (object?)dto.WaardeVeldNummer ?? DBNull.Value);
            command.Parameters.AddWithValue("@WaardeTijd",
                string.IsNullOrWhiteSpace(dto.WaardeTijd) ? DBNull.Value : TimeSpan.Parse(dto.WaardeTijd));
            command.Parameters.AddWithValue("@Prioriteit", dto.Prioriteit ?? 0);
            command.Parameters.AddWithValue("@Actief", dto.Actief ?? true);
            command.Parameters.AddWithValue("@Opmerking", (object?)dto.Opmerking ?? DBNull.Value);

            var rows = await command.ExecuteNonQueryAsync();
            if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });
            return new OkObjectResult(new { id, status = "bijgewerkt" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij bijwerken teamregel {Id}", id);
            return new ObjectResult(new { error = "Bijwerken mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminTeamRegelsDelete")]
    public static async Task<IActionResult> DeleteTeamRegel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/teamregels/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTeamRegelsDelete");
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
                UPDATE [dbo].[TeamRegels] SET [Actief] = 0 WHERE [Id] = @Id AND [ClubCode] = @ClubCode", connection);
            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            var rows = await command.ExecuteNonQueryAsync();

            if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });
            return new OkObjectResult(new { id, status = "soft-deleted" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij verwijderen teamregel {Id}", id);
            return new ObjectResult(new { error = "Verwijderen mislukt" }) { StatusCode = 500 };
        }
    }

    private static IActionResult? ValideerRegel(TeamRegelRequest? dto)
    {
        if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
        if (string.IsNullOrWhiteSpace(dto.TeamNaam))
            return new BadRequestObjectResult(new { error = "TeamNaam verplicht" });
        var toegestaneTypes = new[] { "BufferVoor", "BufferNa", "VoorkeurVeld" };
        if (!toegestaneTypes.Contains(dto.RegelType))
            return new BadRequestObjectResult(new { error = "RegelType moet BufferVoor, BufferNa of VoorkeurVeld zijn" });
        if ((dto.RegelType == "BufferVoor" || dto.RegelType == "BufferNa") && dto.WaardeMinuten == null)
            return new BadRequestObjectResult(new { error = "WaardeMinuten verplicht voor BufferVoor/BufferNa" });
        if (dto.RegelType == "VoorkeurVeld" && dto.WaardeVeldNummer == null)
            return new BadRequestObjectResult(new { error = "WaardeVeldNummer verplicht voor VoorkeurVeld" });
        if (!string.IsNullOrWhiteSpace(dto.WaardeTijd) && !TimeSpan.TryParse(dto.WaardeTijd, out _))
            return new BadRequestObjectResult(new { error = "WaardeTijd vereist HH:mm formaat" });
        return null;
    }

    public class TeamRegelRequest
    {
        public string? TeamNaam { get; set; }
        public string? RegelType { get; set; }
        public int? WaardeMinuten { get; set; }
        public int? WaardeVeldNummer { get; set; }
        public string? WaardeTijd { get; set; }
        public int? Prioriteit { get; set; }
        public bool? Actief { get; set; }
        public string? Opmerking { get; set; }
    }

    private static IActionResult? Valideer(VoorkeurTijdRequest? dto)
    {
        if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
        if (string.IsNullOrWhiteSpace(dto.TeamNaam))
            return new BadRequestObjectResult(new { error = "TeamNaam verplicht" });
        if (dto.DagVanWeek is null or < 1 or > 7)
            return new BadRequestObjectResult(new { error = "DagVanWeek moet 1-7 zijn (6=zaterdag)" });
        if (string.IsNullOrWhiteSpace(dto.VoorkeurTijd) || !TimeSpan.TryParse(dto.VoorkeurTijd, out _))
            return new BadRequestObjectResult(new { error = "VoorkeurTijd vereist HH:mm formaat" });
        return null;
    }

    public class VoorkeurTijdRequest
    {
        public string? TeamNaam { get; set; }
        public int? DagVanWeek { get; set; }
        public string? VoorkeurTijd { get; set; }
        public int? Prioriteit { get; set; }
        public bool? Actief { get; set; }
    }
}
