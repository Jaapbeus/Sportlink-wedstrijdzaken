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
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "admin/voorkeurstijden")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVoorkeurTijdenGet");
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode") ?? "VRC";
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
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    [Function("AdminVoorkeurTijdenPost")]
    public static async Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "admin/voorkeurstijden")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVoorkeurTijdenPost");
        try
        {
            using var bodyReader = new StreamReader(req.Body);
            var dto = JsonConvert.DeserializeObject<VoorkeurTijdRequest>(await bodyReader.ReadToEndAsync());
            var validatie = Valideer(dto);
            if (validatie != null) return validatie;

            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode") ?? "VRC";

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
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    [Function("AdminVoorkeurTijdenPut")]
    public static async Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "admin/voorkeurstijden/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVoorkeurTijdenPut");
        try
        {
            using var bodyReader = new StreamReader(req.Body);
            var dto = JsonConvert.DeserializeObject<VoorkeurTijdRequest>(await bodyReader.ReadToEndAsync());
            var validatie = Valideer(dto);
            if (validatie != null) return validatie;

            await SystemUtilities.WaitForDatabaseAsync(log);

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                UPDATE [dbo].[TeamVoorkeurTijden]
                SET [TeamNaam] = @TeamNaam, [DagVanWeek] = @DagVanWeek, [VoorkeurTijd] = @VoorkeurTijd,
                    [Prioriteit] = @Prioriteit, [Actief] = @Actief, [mta_modified] = GETDATE()
                WHERE [Id] = @Id", connection);
            command.Parameters.AddWithValue("@Id", id);
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
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    [Function("AdminVoorkeurTijdenDelete")]
    public static async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "admin/voorkeurstijden/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminVoorkeurTijdenDelete");
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            // Soft delete: zet Actief = 0
            using var command = new SqlCommand(@"
                UPDATE [dbo].[TeamVoorkeurTijden]
                SET [Actief] = 0, [mta_modified] = GETDATE()
                WHERE [Id] = @Id", connection);
            command.Parameters.AddWithValue("@Id", id);
            var rows = await command.ExecuteNonQueryAsync();

            if (rows == 0)
                return new NotFoundObjectResult(new { error = $"Rij {id} bestaat niet" });

            return new OkObjectResult(new { id, status = "soft-deleted" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij verwijderen voorkeurstijd {Id}", id);
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
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
