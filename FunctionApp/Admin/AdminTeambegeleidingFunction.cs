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

    /// <summary>
    /// POST /api/beheer/teambegeleiding/import — verwerkt een CSV en importeert de begeleiders.
    /// CSV-inhoud wordt in-memory verwerkt en nooit opgeslagen (AVG).
    /// Audit log schrijft alleen metadata (rijen, bestandsnaam, duur) — geen PII.
    /// </summary>
    [Function("AdminTeambegeleidingImport")]
    public static async Task<IActionResult> Import(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/teambegeleiding/import")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTeambegeleidingImport");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            using var bodyReader = new StreamReader(req.Body);
            var body = await bodyReader.ReadToEndAsync();
            var dto = JsonConvert.DeserializeObject<TeambegeleidingImportRequest>(body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.CsvContent))
                return new BadRequestObjectResult(new { error = "csvContent is vereist" });

            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var parseResult = ParseCsv(dto.CsvContent);
            if (!parseResult.IsValid)
                return new BadRequestObjectResult(new
                {
                    error = parseResult.Error,
                    ontbreekt = parseResult.Ontbreekt
                });

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            using (var deleteCmd = new SqlCommand(
                "DELETE FROM [avg].[Teambegeleiding] WHERE [ClubCode] = @ClubCode", connection))
            {
                deleteCmd.Parameters.AddWithValue("@ClubCode", clubCode);
                await deleteCmd.ExecuteNonQueryAsync();
            }

            using (var tx = connection.BeginTransaction())
            {
                foreach (var row in parseResult.Rows)
                {
                    using var ins = new SqlCommand(@"
                        INSERT INTO [avg].[Teambegeleiding]
                            (Team, LeeftijdscategorieTeam, Teamrol, Naam, Emailadres, Telefoonnummer, ClubCode)
                        VALUES
                            (@Team, @Leeftijd, @Teamrol, @Naam, @Email, @Telefoon, @ClubCode)",
                        connection, tx);
                    ins.Parameters.AddWithValue("@Team",     (object?)row.Team ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@Leeftijd", (object?)row.LeeftijdscategorieTeam ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@Teamrol",  (object?)row.Teamrol ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@Naam",     (object?)row.Naam ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@Email",    (object?)row.Emailadres ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@Telefoon", (object?)row.Telefoonnummer ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@ClubCode", clubCode);
                    await ins.ExecuteNonQueryAsync();
                }
                tx.Commit();
            }

            sw.Stop();

            var importeerder = EasyAuthHelper.GetCallerName(req) ?? "admin";
            using (var logCmd = new SqlCommand(@"
                INSERT INTO [avg].[ImportLog] (AantalRijen, CsvBestand, ImporterendeDoor, Duur_ms, ClubCode)
                VALUES (@rijen, @csv, @door, @duur, @club)", connection))
            {
                logCmd.Parameters.AddWithValue("@rijen", parseResult.Rows.Count);
                logCmd.Parameters.AddWithValue("@csv",   (object?)dto.Bestandsnaam ?? DBNull.Value);
                logCmd.Parameters.AddWithValue("@door",  importeerder);
                logCmd.Parameters.AddWithValue("@duur",  (int)sw.ElapsedMilliseconds);
                logCmd.Parameters.AddWithValue("@club",  clubCode);
                await logCmd.ExecuteNonQueryAsync();
            }

            log.LogInformation("Teambegeleiding import geslaagd: {Rijen} rijen (geen PII gelogd — AVG)", parseResult.Rows.Count);

            return new OkObjectResult(new
            {
                rijen         = parseResult.Rows.Count,
                herkend       = parseResult.Herkend,
                ontbreekt     = new List<string>(),
                waarschuwingen = parseResult.Waarschuwingen
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij importeren teambegeleiding (geen PII gelogd — AVG)");
            return new ObjectResult(new { error = "Import mislukt" }) { StatusCode = 500 };
        }
    }

    // ── CSV parsing helpers ───────────────────────────────────────────────────

    private static readonly Dictionary<string, string[]> _kolomAliassen = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Team"]                   = ["Team", "Teamnaam", "Team naam"],
        ["Teamrol"]                = ["Teamrol", "Rol", "Rol in team", "Rol team"],
        ["Roepnaam"]               = ["Roepnaam", "Voornaam", "First name"],
        ["Achternaam"]             = ["Achternaam", "Familienaam", "Last name"],
        ["Emailadres"]             = ["E-mailadres", "Email", "E-mail", "Emailadres", "Mailadres"],
        ["LeeftijdscategorieTeam"] = ["Leeftijdscategorie team", "Leeftijdscategorie", "Age category"],
        ["Tussenvoegsel"]          = ["Tussenvoegsel(s)", "Tussenvoegsel", "Infix", "Tussenv."],
        ["MobielNummer"]           = ["Mobiel nummer", "Mobiel", "Mobiele telefoon", "Mobile"],
        ["TelefoonnummerKolom"]    = ["Telefoonnummer", "Telefoon", "Vaste telefoon", "Phone"],
    };

    private static readonly string[] _vereistKolommen = ["Team", "Teamrol", "Roepnaam", "Achternaam", "Emailadres"];

    private record ImportRij(
        string? Team, string? LeeftijdscategorieTeam, string? Teamrol,
        string? Naam, string? Emailadres, string? Telefoonnummer);

    private class CsvParseResult
    {
        public bool IsValid { get; set; }
        public string? Error { get; set; }
        public List<string> Ontbreekt { get; set; } = [];
        public List<string> Herkend { get; set; } = [];
        public List<string> Waarschuwingen { get; set; } = [];
        public List<ImportRij> Rows { get; set; } = [];
    }

    private static CsvParseResult ParseCsv(string csvContent)
    {
        var result = new CsvParseResult();
        var lines = csvContent
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (lines.Count < 2)
        {
            result.Error = "CSV bevat geen gegevensrijen.";
            return result;
        }

        var headers = SplitCsvLine(lines[0]);

        var mapping = new Dictionary<string, int>();
        foreach (var (canonical, aliases) in _kolomAliassen)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                if (aliases.Any(a => string.Equals(a, headers[i], StringComparison.OrdinalIgnoreCase)))
                {
                    mapping[canonical] = i;
                    break;
                }
            }
        }

        var ontbreekt = _vereistKolommen.Where(v => !mapping.ContainsKey(v)).ToList();
        if (ontbreekt.Count > 0)
        {
            result.IsValid = false;
            result.Ontbreekt = ontbreekt;
            result.Error = $"Vereiste kolommen niet gevonden: {string.Join(", ", ontbreekt)}";
            return result;
        }

        result.Herkend = [.. mapping.Keys];

        if (!mapping.ContainsKey("MobielNummer") && !mapping.ContainsKey("TelefoonnummerKolom"))
            result.Waarschuwingen.Add("Geen telefoonnummer-kolom gevonden — Telefoonnummer wordt leeg.");
        if (!mapping.ContainsKey("LeeftijdscategorieTeam"))
            result.Waarschuwingen.Add("Kolom 'Leeftijdscategorie team' niet gevonden — wordt leeg.");

        for (int i = 1; i < lines.Count; i++)
        {
            var fields = SplitCsvLine(lines[i]);

            string? GetVeld(string key)
            {
                if (!mapping.TryGetValue(key, out var idx) || idx >= fields.Length) return null;
                var v = fields[idx];
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }

            var naamDelen = new[] { GetVeld("Roepnaam"), GetVeld("Tussenvoegsel"), GetVeld("Achternaam") }
                .Where(p => p != null).ToArray();
            var naam = naamDelen.Length > 0 ? string.Join(" ", naamDelen) : null;

            var telefoon = GetVeld("MobielNummer") ?? GetVeld("TelefoonnummerKolom");

            result.Rows.Add(new ImportRij(
                GetVeld("Team"),
                GetVeld("LeeftijdscategorieTeam"),
                GetVeld("Teamrol"),
                naam,
                GetVeld("Emailadres"),
                telefoon));
        }

        result.IsValid = true;
        return result;
    }

    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuote && i + 1 < line.Length && line[i + 1] == '"')
                { current.Append('"'); i++; }
                else
                { inQuote = !inQuote; }
            }
            else if (c == ';' && !inQuote)
            { fields.Add(current.ToString().Trim()); current.Clear(); }
            else
            { current.Append(c); }
        }
        fields.Add(current.ToString().Trim());
        return [.. fields];
    }

    private record DoorsturenRequest(string TeamNaam, string? Onderwerp, string Bericht);
    private record TeambegeleidingImportRequest(string CsvContent, string? Bestandsnaam);
}
