using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminTeambegeleidingFunction.cs</c> (#887).
/// <para>
/// <b>GetTeams, GetBegeleiders en Import zijn volledig vertaald</b>: <c>[avg].[Teambegeleiding]</c>/
/// <c>[avg].[ImportLog]</c> → <c>avg.teambegeleiding</c>/<c>avg.importlog</c> (bestaan al sinds
/// <c>002_avg_teambegeleiding.sql</c>, #824). De CSV-parselogica (kolomaliassen, dedup, validatie)
/// is databasetier-onafhankelijk en ongewijzigd gekopieerd.
/// </para>
/// <para>
/// <b>Doorsturen is bewust NIET vertaald.</b> Die functie hangt af van <c>GraphServiceClient</c>,
/// <c>EmailGraphService</c>, <c>IEmailPersistenceRepository</c> en <c>OntvangerParser</c> — de
/// volledige e-mailverzend-/teamresolutielaag die nog niet bestaat op de Postgres-tier (issue 889,
/// nog niet gestart). Retourneert een expliciete 501 in plaats van een verzending te faken.
/// </para>
/// </summary>
public static class AdminTeambegeleidingFunction
{
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
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            await using var command = new NpgsqlCommand(
                "SELECT DISTINCT team FROM avg.teambegeleiding WHERE team IS NOT NULL AND clubcode = @clubcode ORDER BY team",
                connection);
            command.Parameters.AddWithValue("clubcode", clubCode);
            await using var reader = await command.ExecuteReaderAsync();
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
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            await using var command = new NpgsqlCommand(@"
                SELECT naam, teamrol, emailadres, telefoonnummer
                FROM avg.teambegeleiding
                WHERE team = @team
                  AND clubcode = @clubcode
                ORDER BY
                    CASE WHEN teamrol LIKE '%Trainer%' THEN 1
                         WHEN teamrol LIKE '%Coach%' THEN 2
                         WHEN teamrol LIKE '%Teamleider%' THEN 3
                         ELSE 4 END,
                    naam
            ", connection);
            command.Parameters.AddWithValue("team", team);
            command.Parameters.AddWithValue("clubcode", clubCode);
            await using var reader = await command.ExecuteReaderAsync();
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

    [Function("AdminTeambegeleidingDoorsturen")]
    public static Task<IActionResult> Doorsturen(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/teambegeleiding/doorsturen")] HttpRequest req,
        FunctionContext context)
    {
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return Task.FromResult(authResult);

        return Task.FromResult<IActionResult>(new ObjectResult(new
        {
            error = "Doorsturen is nog niet beschikbaar op de Postgres-tier — hangt af van de " +
                    "e-mailverzend-/teamresolutielaag uit issue 889 (nog niet gestart)."
        })
        { StatusCode = 501 });
    }

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
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

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

            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            await using (var deleteCmd = new NpgsqlCommand(
                "DELETE FROM avg.teambegeleiding WHERE clubcode = @clubcode", connection))
            {
                deleteCmd.Parameters.AddWithValue("clubcode", clubCode);
                await deleteCmd.ExecuteNonQueryAsync();
            }

            await using (var tx = await connection.BeginTransactionAsync())
            {
                foreach (var row in parseResult.Rows)
                {
                    await using var ins = new NpgsqlCommand(@"
                        INSERT INTO avg.teambegeleiding
                            (team, leeftijdscategorieteam, teamrol, naam, emailadres, telefoonnummer, clubcode)
                        VALUES
                            (@team, @leeftijd, @teamrol, @naam, @email, @telefoon, @clubcode)",
                        connection, tx);
                    ins.Parameters.AddWithValue("team",     (object?)row.Team ?? DBNull.Value);
                    ins.Parameters.AddWithValue("leeftijd", (object?)row.LeeftijdscategorieTeam ?? DBNull.Value);
                    ins.Parameters.AddWithValue("teamrol",  (object?)row.Teamrol ?? DBNull.Value);
                    ins.Parameters.AddWithValue("naam",     (object?)row.Naam ?? DBNull.Value);
                    ins.Parameters.AddWithValue("email",    (object?)row.Emailadres ?? DBNull.Value);
                    ins.Parameters.AddWithValue("telefoon", (object?)row.Telefoonnummer ?? DBNull.Value);
                    ins.Parameters.AddWithValue("clubcode", clubCode);
                    await ins.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            }

            sw.Stop();

            var importeerder = EasyAuthHelper.GetCallerName(req) ?? "admin";
            await using (var logCmd = new NpgsqlCommand(@"
                INSERT INTO avg.importlog (aantalrijen, csvbestand, importerendedoor, duur_ms, clubcode)
                VALUES (@rijen, @csv, @door, @duur, @club)", connection))
            {
                logCmd.Parameters.AddWithValue("rijen", parseResult.Rows.Count);
                logCmd.Parameters.AddWithValue("csv",   (object?)dto.Bestandsnaam ?? DBNull.Value);
                logCmd.Parameters.AddWithValue("door",  importeerder);
                logCmd.Parameters.AddWithValue("duur",  (int)sw.ElapsedMilliseconds);
                logCmd.Parameters.AddWithValue("club",  clubCode);
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

    // ── CSV parsing helpers (databasetier-onafhankelijk, ongewijzigd) ─────────

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

    internal record ImportRij(
        string? Team, string? LeeftijdscategorieTeam, string? Teamrol,
        string? Naam, string? Emailadres, string? Telefoonnummer);

    internal class CsvParseResult
    {
        public bool IsValid { get; set; }
        public string? Error { get; set; }
        public List<string> Ontbreekt { get; set; } = [];
        public List<string> Herkend { get; set; } = [];
        public List<string> Waarschuwingen { get; set; } = [];
        public List<ImportRij> Rows { get; set; } = [];
    }

    internal static CsvParseResult ParseCsv(string csvContent)
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

        var voorDedup = result.Rows.Count;
        result.Rows = [.. result.Rows
            .GroupBy(r => (
                Team: r.Team?.Trim().ToUpperInvariant(),
                Teamrol: r.Teamrol?.Trim().ToUpperInvariant(),
                Naam: r.Naam?.Trim().ToUpperInvariant(),
                Email: r.Emailadres?.Trim().ToUpperInvariant(),
                Telefoon: r.Telefoonnummer?.Trim().ToUpperInvariant()))
            .Select(g => g.First())];
        var duplicaten = voorDedup - result.Rows.Count;
        if (duplicaten > 0)
            result.Waarschuwingen.Add(
                $"{duplicaten} exacte duplicaat-rij{(duplicaten == 1 ? "" : "en")} overgeslagen (zelfde team, rol, naam en e-mailadres).");

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

    private record TeambegeleidingImportRequest(string CsvContent, string? Bestandsnaam);
}
