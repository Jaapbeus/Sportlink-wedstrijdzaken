using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Admin;

/// <summary>
/// Geeft de lijst van beschikbare clubs terug (uit dbo.AppSettings).
/// Gebruikt door de Blazor Admin UI voor de club-selector dropdown in de topbalk.
///
/// GET /api/beheer/clubs → ClubDto[]
/// </summary>
public static class AdminClubsFunction
{
    [Function("AdminClubsGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/clubs")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminClubsGet");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;

        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(@"
                SELECT [ClubCode], [ClubName]
                FROM [dbo].[AppSettings]
                ORDER BY [SyncEnabled] DESC, [ClubName]",
                connection);

            using var reader = await command.ExecuteReaderAsync();
            var clubs = new List<object>();
            while (await reader.ReadAsync())
            {
                clubs.Add(new
                {
                    clubCode = reader.GetString(0),
                    clubName = reader.GetString(1)
                });
            }

            return new OkObjectResult(clubs);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen clubs");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }
}
