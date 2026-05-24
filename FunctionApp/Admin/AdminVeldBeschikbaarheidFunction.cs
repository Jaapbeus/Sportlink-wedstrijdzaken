using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor VeldBeschikbaarheid (openingstijden sportpark per veld per dag). v2.
/// </summary>
public static class AdminVeldBeschikbaarheidFunction
{
    [Function("AdminVeldBeschikbaarheidGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/veldbeschikbaarheid")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVeldBeschikbaarheidGet");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT vb.[Id], vb.[VeldNummer], v.[VeldNaam], vb.[DagVanWeek],
                       CONVERT(VARCHAR(5), vb.[BeschikbaarVanaf]) AS [BeschikbaarVanaf],
                       CONVERT(VARCHAR(5), vb.[BeschikbaarTot])   AS [BeschikbaarTot],
                       vb.[GebruikZonsondergang]
                FROM [dbo].[VeldBeschikbaarheid] vb
                JOIN [dbo].[Velden] v ON v.[VeldNummer] = vb.[VeldNummer]
                WHERE vb.[ClubCode] = @ClubCode
                ORDER BY vb.[DagVanWeek], vb.[VeldNummer]", connection);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            using var reader = await command.ExecuteReaderAsync();
            var list = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                list.Add(row);
            }
            return new OkObjectResult(list);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen veldbeschikbaarheid");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminVeldBeschikbaarheidPut")]
    public static async Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/veldbeschikbaarheid/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVeldBeschikbaarheidPut");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            using var bodyReader = new StreamReader(req.Body);
            var dto = JsonConvert.DeserializeObject<VeldBeschikbaarheidRequest>(await bodyReader.ReadToEndAsync());
            var validatie = Valideer(dto);
            if (validatie != null) return validatie;

            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                UPDATE [dbo].[VeldBeschikbaarheid]
                SET [BeschikbaarVanaf]     = @Vanf,
                    [BeschikbaarTot]       = @Tot,
                    [GebruikZonsondergang] = @Zon
                WHERE [Id] = @Id AND [ClubCode] = @ClubCode", connection);
            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            command.Parameters.AddWithValue("@Vanf", TimeSpan.Parse(dto!.BeschikbaarVanaf!));
            command.Parameters.AddWithValue("@Tot", TimeSpan.Parse(dto.BeschikbaarTot!));
            command.Parameters.AddWithValue("@Zon", dto.GebruikZonsondergang);

            var rows = await command.ExecuteNonQueryAsync();
            if (rows == 0)
                return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });

            return new OkObjectResult(new { id, status = "bijgewerkt" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij bijwerken veldbeschikbaarheid {Id}", id);
            return new ObjectResult(new { error = "Bijwerken mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminVeldenGet")]
    public static async Task<IActionResult> GetVelden(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/velden")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVeldenGet");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(
                "SELECT [VeldNummer], [VeldNaam] FROM [dbo].[Velden] WHERE [ClubCode] = @ClubCode ORDER BY [VeldNummer]",
                connection);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            using var reader = await command.ExecuteReaderAsync();
            var list = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
                list.Add(new() { ["VeldNummer"] = reader.GetInt32(0), ["VeldNaam"] = reader.GetString(1) });
            return new OkObjectResult(list);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen velden");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminVeldBeschikbaarheidPost")]
    public static async Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/veldbeschikbaarheid")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVeldBeschikbaarheidPost");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            using var bodyReader = new StreamReader(req.Body);
            var dto = JsonConvert.DeserializeObject<VeldBeschikbaarheidCreateRequest>(await bodyReader.ReadToEndAsync());
            if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
            if (dto.VeldNummer <= 0) return new BadRequestObjectResult(new { error = "VeldNummer vereist" });
            if (dto.DagVanWeek < 1 || dto.DagVanWeek > 7) return new BadRequestObjectResult(new { error = "DagVanWeek moet 1–7 zijn" });
            var tijdenValidatie = ValideerTijden(dto.BeschikbaarVanaf, dto.BeschikbaarTot);
            if (tijdenValidatie != null) return tijdenValidatie;

            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            // Uniciteitscheck: zelfde veld + dag + club mag niet dubbel
            using var checkCmd = new SqlCommand(@"
                SELECT COUNT(1) FROM [dbo].[VeldBeschikbaarheid]
                WHERE [VeldNummer] = @VeldNummer AND [DagVanWeek] = @Dag AND [ClubCode] = @ClubCode",
                connection);
            checkCmd.Parameters.AddWithValue("@VeldNummer", dto.VeldNummer);
            checkCmd.Parameters.AddWithValue("@Dag", dto.DagVanWeek);
            checkCmd.Parameters.AddWithValue("@ClubCode", clubCode);
            var count = (int)(await checkCmd.ExecuteScalarAsync())!;
            if (count > 0)
                return new ConflictObjectResult(new { error = "Combinatie veld + dag bestaat al" });

            using var insertCmd = new SqlCommand(@"
                INSERT INTO [dbo].[VeldBeschikbaarheid]
                    ([VeldNummer], [DagVanWeek], [BeschikbaarVanaf], [BeschikbaarTot], [GebruikZonsondergang], [ClubCode])
                OUTPUT INSERTED.[Id]
                VALUES (@VeldNummer, @Dag, @Vanf, @Tot, @Zon, @ClubCode)",
                connection);
            insertCmd.Parameters.AddWithValue("@VeldNummer", dto.VeldNummer);
            insertCmd.Parameters.AddWithValue("@Dag", dto.DagVanWeek);
            insertCmd.Parameters.AddWithValue("@Vanf", TimeSpan.Parse(dto.BeschikbaarVanaf!));
            insertCmd.Parameters.AddWithValue("@Tot", TimeSpan.Parse(dto.BeschikbaarTot!));
            insertCmd.Parameters.AddWithValue("@Zon", dto.GebruikZonsondergang);
            insertCmd.Parameters.AddWithValue("@ClubCode", clubCode);

            var newId = (int)(await insertCmd.ExecuteScalarAsync())!;
            return new OkObjectResult(new { id = newId, status = "aangemaakt" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij aanmaken veldbeschikbaarheid");
            return new ObjectResult(new { error = "Aanmaken mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminVeldBeschikbaarheidDelete")]
    public static async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/veldbeschikbaarheid/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVeldBeschikbaarheidDelete");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(
                "DELETE FROM [dbo].[VeldBeschikbaarheid] WHERE [Id] = @Id AND [ClubCode] = @ClubCode",
                connection);
            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            var rows = await command.ExecuteNonQueryAsync();
            if (rows == 0) return new NotFoundObjectResult(new { error = $"Rij {id} niet gevonden" });
            return new OkObjectResult(new { id, status = "verwijderd" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij verwijderen veldbeschikbaarheid {Id}", id);
            return new ObjectResult(new { error = "Verwijderen mislukt" }) { StatusCode = 500 };
        }
    }

    private static IActionResult? Valideer(VeldBeschikbaarheidRequest? dto)
    {
        if (dto == null) return new BadRequestObjectResult(new { error = "Lege body" });
        return ValideerTijden(dto.BeschikbaarVanaf, dto.BeschikbaarTot);
    }

    private static IActionResult? ValideerTijden(string? vanf, string? tot)
    {
        if (string.IsNullOrWhiteSpace(vanf) || !TimeSpan.TryParse(vanf, out _))
            return new BadRequestObjectResult(new { error = "BeschikbaarVanaf vereist HH:mm formaat" });
        if (string.IsNullOrWhiteSpace(tot) || !TimeSpan.TryParse(tot, out _))
            return new BadRequestObjectResult(new { error = "BeschikbaarTot vereist HH:mm formaat" });
        return null;
    }

    public class VeldBeschikbaarheidRequest
    {
        public string? BeschikbaarVanaf { get; set; }
        public string? BeschikbaarTot { get; set; }
        public bool GebruikZonsondergang { get; set; }
    }

    public class VeldBeschikbaarheidCreateRequest
    {
        public int VeldNummer { get; set; }
        public int DagVanWeek { get; set; }
        public string? BeschikbaarVanaf { get; set; }
        public string? BeschikbaarTot { get; set; }
        public bool GebruikZonsondergang { get; set; }
    }
}
