using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Admin;

/// <summary>
/// Levert de lijst van teams uit his.teams (gesynchroniseerd via Sportlink API), gefilterd op ClubCode.
/// Gebruikt door de Blazor Admin UI voor dropdowns in voorkeurstijden en teamregels.
/// </summary>
public static class AdminTeamsFunction
{
    [Function("AdminTeamsGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/teams")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTeamsGet");
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
                SELECT DISTINCT [teamnaam]
                FROM [his].[teams]
                WHERE [ClubCode] = @ClubCode
                ORDER BY [teamnaam]",
                connection);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            using var reader = await command.ExecuteReaderAsync();
            var teams = new List<string>();
            while (await reader.ReadAsync())
                teams.Add(reader.GetString(0));

            return new OkObjectResult(teams);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen teams");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }
}
