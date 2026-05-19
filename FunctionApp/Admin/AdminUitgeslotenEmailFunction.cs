using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor UitgeslotenEmailAdressen — expliciet uitgesloten afzenders. v2.
/// </summary>
public static class AdminUitgeslotenEmailFunction
{
    [Function("AdminUitgeslotenEmailGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/uitgesloten-emails")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminUitgeslotenEmailGet");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(
                @"SELECT [Id], [EmailAdres], [Omschrijving], [Actief], [ClubCode],
                         [mta_inserted]
                  FROM [dbo].[UitgeslotenEmailAdressen]
                  WHERE [ClubCode] = @ClubCode
                  ORDER BY [EmailAdres]", connection);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            using var reader = await command.ExecuteReaderAsync();
            var list = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                list.Add(new Dictionary<string, object?>
                {
                    ["id"]           = reader.GetInt32(reader.GetOrdinal("Id")),
                    ["emailAdres"]   = reader.GetString(reader.GetOrdinal("EmailAdres")),
                    ["omschrijving"] = reader.IsDBNull(reader.GetOrdinal("Omschrijving")) ? null : reader.GetString(reader.GetOrdinal("Omschrijving")),
                    ["actief"]       = reader.GetBoolean(reader.GetOrdinal("Actief")),
                    ["clubCode"]     = reader.GetString(reader.GetOrdinal("ClubCode")),
                });
            }
            return new OkObjectResult(list);
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            // Tabel bestaat nog niet — post-deployment script nog niet uitgevoerd
            log.LogWarning("UitgeslotenEmailAdressen tabel bestaat nog niet — lege lijst teruggegeven");
            return new OkObjectResult(new List<object>());
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen uitsluitingslijst");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminUitgeslotenEmailPost")]
    public static async Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/uitgesloten-emails")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminUitgeslotenEmailPost");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");
            var body = await new System.IO.StreamReader(req.Body).ReadToEndAsync();
            var dto = JsonConvert.DeserializeObject<UitgeslotenEmailRequest>(body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.EmailAdres))
                return new BadRequestObjectResult(new { error = "EmailAdres verplicht" });

            var adres = dto.EmailAdres.Trim().ToLowerInvariant();

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(
                @"INSERT INTO [dbo].[UitgeslotenEmailAdressen] ([EmailAdres], [Omschrijving], [Actief], [ClubCode])
                  VALUES (@EmailAdres, @Omschrijving, @Actief, @ClubCode);
                  SELECT CAST(SCOPE_IDENTITY() AS INT);", connection);
            command.Parameters.AddWithValue("@EmailAdres", adres);
            command.Parameters.AddWithValue("@Omschrijving", (object?)dto.Omschrijving ?? DBNull.Value);
            command.Parameters.AddWithValue("@Actief", dto.Actief);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            var newId = (int)(await command.ExecuteScalarAsync())!;
            log.LogInformation("Uitsluitingsadres {Adres} toegevoegd (id={Id})", adres, newId);
            return new OkObjectResult(new { id = newId });
        }
        catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
        {
            return new ConflictObjectResult(new { error = "Dit e-mailadres staat al in de lijst" });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij toevoegen uitsluitingsadres");
            return new ObjectResult(new { error = "Opslaan mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminUitgeslotenEmailDelete")]
    public static async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/uitgesloten-emails/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminUitgeslotenEmailDelete");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(
                "DELETE FROM [dbo].[UitgeslotenEmailAdressen] WHERE [Id] = @Id AND [ClubCode] = @ClubCode", connection);
            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            var rows = await command.ExecuteNonQueryAsync();
            if (rows == 0) return new NotFoundObjectResult(new { error = "Niet gevonden" });
            log.LogInformation("Uitsluitingsadres id={Id} verwijderd", id);
            return new OkObjectResult(new { deleted = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij verwijderen uitsluitingsadres id={Id}", id);
            return new ObjectResult(new { error = "Verwijderen mislukt" }) { StatusCode = 500 };
        }
    }

    private class UitgeslotenEmailRequest
    {
        public string? EmailAdres { get; set; }
        public string? Omschrijving { get; set; }
        public bool Actief { get; set; } = true;
    }
}
