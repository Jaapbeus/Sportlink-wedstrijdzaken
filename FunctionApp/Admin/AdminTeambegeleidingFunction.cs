using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Newtonsoft.Json;
using SportlinkFunction.Email;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor teambegeleiding (#168, #299).
/// /teambegeleiding/{team} geeft Naam, Teamrol, Emailadres en Telefoonnummer terug
/// aan de ingelogde beheerder (admin/user-rol) — pagina is afgeschermd achter Entra ID Easy Auth.
/// E-mail doorstuur-pipeline gebruikt Emailadres uitsluitend server-side; nooit in auto-reply body.
/// </summary>
public static class AdminTeambegeleidingFunction
{
    /// <summary>
    /// GET /api/beheer/teambegeleiding — lijst van alle teams waarvoor begeleiding beschikbaar is.
    /// </summary>
    [Function("AdminTeambegeleidingTeams")]
    public static async Task<IActionResult> GetTeams(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/teambegeleiding")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTeambegeleidingTeams");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            using var command = new SqlCommand(
                "SELECT DISTINCT [Team] FROM [avg].[Teambegeleiding] WHERE [Team] IS NOT NULL AND [ClubCode] = @ClubCode ORDER BY [Team]",
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
            log.LogError(ex, "Fout bij ophalen teams uit teambegeleiding");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// GET /api/beheer/teambegeleiding/{team} — begeleiders voor een specifiek team.
    /// Response bevat Naam, Teamrol, Emailadres en Telefoonnummer — alleen voor ingelogde beheerders (#299).
    /// </summary>
    [Function("AdminTeambegeleidingGet")]
    public static async Task<IActionResult> GetBegeleiders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/teambegeleiding/{team}")] HttpRequest req,
        string team,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTeambegeleidingGet");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            using var command = new SqlCommand(@"
                SELECT [Naam], [Teamrol], [Emailadres], [Telefoonnummer]
                FROM [avg].[Teambegeleiding]
                WHERE [Team] = @team
                  AND [ClubCode] = @ClubCode
                ORDER BY
                    CASE WHEN [Teamrol] LIKE '%Trainer%' THEN 1
                         WHEN [Teamrol] LIKE '%Coach%' THEN 2
                         WHEN [Teamrol] LIKE '%Teamleider%' THEN 3
                         ELSE 4 END,
                    [Naam]
            ", connection);
            command.Parameters.AddWithValue("@team", team);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            using var reader = await command.ExecuteReaderAsync();
            var list = new List<object>();
            while (await reader.ReadAsync())
            {
                list.Add(new
                {
                    Naam = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    Teamrol = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Emailadres = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Telefoonnummer = reader.IsDBNull(3) ? null : reader.GetString(3)
                });
            }
            return new OkObjectResult(list);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen begeleiders (team niet gelogd — AVG)");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// POST /api/beheer/teambegeleiding/doorsturen — stuurt een vraag door naar de begeleiding.
    /// Coach-email wordt server-side opgezocht en nooit in de response opgenomen (AVG).
    /// </summary>
    [Function("AdminTeambegeleidingDoorsturen")]
    public static async Task<IActionResult> Doorsturen(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/teambegeleiding/doorsturen")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTeambegeleidingDoorsturen");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            using var bodyReader = new StreamReader(req.Body);
            var body = await bodyReader.ReadToEndAsync();
            var dto = JsonConvert.DeserializeObject<DoorsturenRequest>(body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.TeamNaam))
                return new BadRequestObjectResult(new { error = "TeamNaam is vereist" });
            if (string.IsNullOrWhiteSpace(dto.Bericht))
                return new BadRequestObjectResult(new { error = "Bericht is vereist" });

            // Naam + email aanvrager uit Entra claims (server-side — nooit in response)
            var aanvragerNaam = EasyAuthHelper.GetCallerName(req) ?? "een club-gebruiker";
            var aanvragerEmail = EasyAuthHelper.GetCallerEmail(req);

            // Coach-email server-side ophalen (NOOIT in response)
            string? coachEmail = null;
            using (var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString))
            {
                await connection.OpenAsync();
                var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
                using var cmd = new SqlCommand(@"
                    SELECT TOP 1 [Emailadres]
                    FROM [avg].[Teambegeleiding]
                    WHERE [Team] = @team
                      AND [Emailadres] IS NOT NULL
                      AND [ClubCode] = @ClubCode
                    ORDER BY
                        CASE WHEN [Teamrol] LIKE '%Trainer%' THEN 1
                             WHEN [Teamrol] LIKE '%Coach%' THEN 2
                             WHEN [Teamrol] LIKE '%Teamleider%' THEN 3
                             ELSE 4 END
                ", connection);
                cmd.Parameters.AddWithValue("@team", dto.TeamNaam);
                cmd.Parameters.AddWithValue("@ClubCode", clubCode);
                var result = await cmd.ExecuteScalarAsync();
                coachEmail = result as string;
            }

            // Coördinator-email ophalen uit AppSettings
            var coordinatorEmail = SystemUtilities.AppSettings.GetSetting("plannerEmailAdres");

            if (string.IsNullOrEmpty(coachEmail))
            {
                // Geen coach gevonden: stuur naar coördinator als fallback
                if (string.IsNullOrEmpty(coordinatorEmail))
                    return new ObjectResult(new { error = "Geen begeleider en geen coördinator geconfigureerd" }) { StatusCode = 503 };
                coachEmail = coordinatorEmail;
                log.LogWarning("Geen coach-email gevonden voor team — doorgestuurd naar coordinator");
            }

            var graphClient = context.InstanceServices.GetService<GraphServiceClient>();
            if (graphClient == null)
            {
                log.LogWarning("Graph SDK niet geconfigureerd — e-mail doorsturen niet mogelijk");
                return new ObjectResult(new { error = "E-mail service niet geconfigureerd" }) { StatusCode = 503 };
            }

            var loggerFactory = context.InstanceServices.GetRequiredService<ILoggerFactory>();
            var emailService = new EmailGraphService(graphClient, loggerFactory.CreateLogger<EmailGraphService>());

            var subject = $"[{dto.TeamNaam}] Vraag van {aanvragerNaam}";
            var htmlBody = $@"<p>Er is een vraag binnengekomen over de begeleiding van <strong>{System.Net.WebUtility.HtmlEncode(dto.TeamNaam)}</strong>.</p>
<p><strong>Vraagsteller:</strong> {System.Net.WebUtility.HtmlEncode(aanvragerNaam)}</p>
<p><strong>Onderwerp:</strong> {System.Net.WebUtility.HtmlEncode(dto.Onderwerp ?? "")}</p>
<hr />
<p>{System.Net.WebUtility.HtmlEncode(dto.Bericht).Replace("\n", "<br />")}</p>
<hr />
<p><em>U kunt direct antwoorden op dit bericht — uw antwoord gaat naar de vraagsteller.</em></p>";

            await emailService.StuurTeamContactDoorAsync(coachEmail, subject, htmlBody, aanvragerEmail, coordinatorEmail);

            return new OkObjectResult(new
            {
                success = true,
                bericht = $"Uw vraag over de begeleiding van {dto.TeamNaam} is doorgestuurd. De begeleider neemt rechtstreeks contact met u op."
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij doorsturen teambegeleiding-vraag (geen PII gelogd — AVG)");
            return new ObjectResult(new { error = "Doorsturen mislukt" }) { StatusCode = 500 };
        }
    }

    private record DoorsturenRequest(string TeamNaam, string? Onderwerp, string Bericht);
}
