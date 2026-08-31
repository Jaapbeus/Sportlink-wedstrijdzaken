using FunctionApp.Postgres.Email;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Npgsql;
using Planner.Shared;

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
/// <b>Databaselaag van Import gedelegeerd naar <see cref="Database.Postgres.TeambegeleidingImporter"/>
/// (issue 913, ná #887).</b> Deze klasse implementeerde die laag oorspronkelijk zelf, met een
/// niet-atomische delete + per-rij-insert-lus + losse auditlog-insert — terwijl #824 precies deze
/// laag al had gebouwd, gereviewd en empirisch getest mét transactionele atomiciteit. Twee
/// onafhankelijke implementaties van dezelfde AVG-gevoelige databasebewerking op dezelfde tier is
/// exact het soort duplicatie dat CLAUDE.md's architectuurregels willen voorkomen — nu opgelost
/// door deze klasse uitsluitend nog CSV te parsen en de al bestaande, geharde implementatie aan te
/// roepen.
/// </para>
/// <para>
/// <b>Doorsturen is sinds issue 888 vervolg (§43) ook vertaald</b> — daarmee heeft deze tier geen
/// enkel 501-endpoint meer. De uitgaande e-mail loopt via
/// <see cref="FunctionApp.Postgres.Email.IEmailGraphService"/>, dat in <c>Program.cs</c> alleen
/// wordt geregistreerd als de Graph-secrets geconfigureerd zijn én
/// <c>EgressGuard.ExternalIntegrationsAllowed()</c> true is (#857). Is dat niet zo, dan geeft dit
/// endpoint een eerlijke 503 — geen gefakete "verstuurd"-melding.
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
                    -- ILIKE, niet LIKE (§43): op de SQL Server-tier is LIKE hoofdletterongevoelig
                    -- via de Latin1_General_CI_AS-collatie, op Postgres niet. De teamrol komt uit
                    -- een handmatig aangeleverde CSV-import, dus een rol in kleine letters komt in
                    -- de praktijk voor — met LIKE viel die stilzwijgend in de ELSE-tak en stond de
                    -- trainer onderaan in plaats van bovenaan. Zelfde klasse fout als #820.
                    CASE WHEN teamrol ILIKE '%Trainer%' THEN 1
                         WHEN teamrol ILIKE '%Coach%' THEN 2
                         WHEN teamrol ILIKE '%Teamleider%' THEN 3
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

    /// <summary>
    /// Stuurt een vraag over teambegeleiding door naar de begeleider(s) — Postgres-vertaling van
    /// het gelijknamige SQL Server-endpoint (issue 888 vervolg, §43).
    /// <para>
    /// AVG: e-mailadressen van begeleiders worden server-side opgezocht en komen <b>nooit</b> in de
    /// respons of in een logregel. Vrij ingetypte ontvangers worden vastgelegd in de audittrail,
    /// net als op de SQL Server-tier.
    /// </para>
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
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            using var bodyReader = new StreamReader(req.Body);
            var body = await bodyReader.ReadToEndAsync();
            var dto = JsonConvert.DeserializeObject<DoorsturenRequest>(body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.TeamNaam))
                return new BadRequestObjectResult(new { error = "TeamNaam is vereist" });
            if (string.IsNullOrWhiteSpace(dto.Bericht))
                return new BadRequestObjectResult(new { error = "Bericht is vereist" });

            // Naam + e-mail van de aanvrager uit de Entra-claims (server-side — nooit in de respons)
            var aanvragerNaam = EasyAuthHelper.GetCallerName(req) ?? "een club-gebruiker";
            var aanvragerEmail = EasyAuthHelper.GetCallerEmail(req);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            var cs = PostgresDatabaseConfig.ConnectionString;

            // #765: "Email Aan" bepaalt de ontvangers zodra het veld gevuld is. Leeg/afwezig veld
            // houdt het oude gedrag (server-side lookup + coördinator-fallback) intact.
            List<string>? ontvangers = null;
            if (!string.IsNullOrWhiteSpace(dto.Ontvangers))
            {
                var parseResultaat = OntvangerParser.Parse(dto.Ontvangers);
                if (!parseResultaat.IsValid)
                    return new BadRequestObjectResult(new { error = parseResultaat.FoutMelding });

                var uitgesloten = await SqlEmailPersistenceRepository.GetExcludedEmailAddressesAsync(cs, clubCode);
                var geweigerdAdres = parseResultaat.Emailadressen.FirstOrDefault(uitgesloten.Contains);
                if (geweigerdAdres != null)
                    return new BadRequestObjectResult(new
                    {
                        error = $"E-mailadres \"{geweigerdAdres}\" staat op de uitsluitingslijst en kan niet als ontvanger worden gebruikt."
                    });

                ontvangers = [.. parseResultaat.Emailadressen];
            }

            var coordinatorEmail = PostgresAppSettings.GetSetting("plannerEmailAdres");

            if (ontvangers == null)
            {
                var begeleiderEmail = await ZoekBegeleiderEmailAsync(cs, dto.TeamNaam, clubCode);

                if (string.IsNullOrEmpty(begeleiderEmail))
                {
                    if (string.IsNullOrEmpty(coordinatorEmail))
                        return new ObjectResult(new { error = "Geen begeleider en geen coördinator geconfigureerd" }) { StatusCode = 503 };
                    begeleiderEmail = coordinatorEmail;
                    log.LogWarning("Geen begeleider-e-mail gevonden voor team — doorgestuurd naar coördinator");
                }
                ontvangers = [begeleiderEmail];
            }

            // EgressGuard (#857): buiten productie is IEmailGraphService onvoorwaardelijk
            // ongeregistreerd, ook met geconfigureerde Graph-secrets. Niet geregistreerd → 503, geen
            // gefakete "verstuurd"-melding.
            var emailService = context.InstanceServices.GetService<IEmailGraphService>();
            if (emailService == null)
            {
                log.LogWarning("Graph SDK niet geconfigureerd — e-mail doorsturen niet mogelijk");
                return new ObjectResult(new { error = "E-mail service niet geconfigureerd" }) { StatusCode = 503 };
            }

            var subject = $"[{dto.TeamNaam}] Vraag van {aanvragerNaam}";
            var htmlBody = $@"<p>Er is een vraag binnengekomen over de begeleiding van <strong>{System.Net.WebUtility.HtmlEncode(dto.TeamNaam)}</strong>.</p>
<p><strong>Vraagsteller:</strong> {System.Net.WebUtility.HtmlEncode(aanvragerNaam)}</p>
<p><strong>Onderwerp:</strong> {System.Net.WebUtility.HtmlEncode(dto.Onderwerp ?? "")}</p>
<hr />
<p>{System.Net.WebUtility.HtmlEncode(dto.Bericht).Replace("\n", "<br />")}</p>
<hr />
<p><em>U kunt direct antwoorden op dit bericht — uw antwoord gaat naar de vraagsteller.</em></p>";

            await emailService.StuurTeamContactDoorAsync(ontvangers, subject, htmlBody, aanvragerEmail, coordinatorEmail);

            // Audit-trail (#765): vrij ingetypte ontvangers zijn nieuwe persoonsgegevens. De mail is
            // hierboven al verstuurd — een fout in het wegschrijven van de audit-rij mag de
            // geslaagde verzending niet alsnog als mislukt melden (dat zou tot een dubbele
            // verzendpoging kunnen leiden).
            try
            {
                await SqlEmailPersistenceRepository.InsertTeambegeleidingDoorsturenAuditAsync(
                    cs, dto.TeamNaam, aanvragerEmail ?? "onbekend", string.Join("; ", ontvangers), clubCode);
            }
            catch (Exception auditEx)
            {
                log.LogWarning(auditEx, "Audit-trail voor teambegeleiding-doorsturen kon niet worden weggeschreven (verzending zelf is wel geslaagd)");
            }

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
    /// Zoekt het e-mailadres van de meest geschikte begeleider van een team (AVG: nooit in respons
    /// of log). Zelfde rolvoorkeur als het SQL Server-origineel: trainer vóór coach vóór teamleider
    /// vóór de rest. <c>ILIKE</c> in plaats van <c>LIKE</c> — de rolomschrijving komt uit een
    /// handmatige CSV-import en Postgres' <c>LIKE</c> is hoofdlettergevoelig (#820).
    /// </summary>
    internal static async Task<string?> ZoekBegeleiderEmailAsync(string connectionString, string teamNaam, string clubCode)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT emailadres
            FROM avg.teambegeleiding
            WHERE team = @team
              AND emailadres IS NOT NULL
              AND clubcode = @clubcode
            ORDER BY
                CASE WHEN teamrol ILIKE '%Trainer%'    THEN 1
                     WHEN teamrol ILIKE '%Coach%'      THEN 2
                     WHEN teamrol ILIKE '%Teamleider%' THEN 3
                     ELSE 4 END
            LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("team", teamNaam);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private record DoorsturenRequest(string TeamNaam, string? Onderwerp, string Bericht, string? Ontvangers);

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

            var parseResult = ParseCsv(dto.CsvContent);
            if (!parseResult.IsValid)
                return new BadRequestObjectResult(new
                {
                    error = parseResult.Error,
                    ontbreekt = parseResult.Ontbreekt
                });

            // Databaselaag gedelegeerd naar Database.Postgres.TeambegeleidingImporter (issue 824)
            // in plaats van een eigen, niet-atomische delete/insert/auditlog-implementatie (issue
            // 913: dat was hier eerder drie losse, niet-getransactioneerde stappen — een crash
            // tussen de delete en de insert-lus liet de club zonder teambegeleidingsdata achter).
            // ParseCsv's ImportRij en TeambegeleidingImporter's TeambegeleidingRow hebben dezelfde
            // zes velden; alleen de CSV-parselogica (kolomherkenning, aliassen) blijft hier staan.
            var rows = parseResult.Rows
                .Select(r => new Database.Postgres.TeambegeleidingRow(
                    r.Team, r.LeeftijdscategorieTeam, r.Teamrol, r.Naam, r.Emailadres, r.Telefoonnummer))
                .ToList();

            var importeerder = EasyAuthHelper.GetCallerName(req) ?? "admin";

            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            var importResult = await Database.Postgres.TeambegeleidingImporter.ImportAsync(
                connection, clubCode, rows, dto.Bestandsnaam, importeerder);

            log.LogInformation("Teambegeleiding import geslaagd: {Rijen} rijen in {Duur}ms (geen PII gelogd — AVG)",
                importResult.AantalRijen, importResult.DuurMs);

            return new OkObjectResult(new
            {
                rijen         = importResult.AantalRijen,
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
