using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;
using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminTestDataFunction.cs</c> (#952). Bewuste
/// kopie, geen gedeelde abstractie (ARCHITECTUUR-DATABASE-TIERS.md §2).
/// <para>
/// <b>Ontwerpkeuze — Optie B</b> (endpoint vertaalt de sleutel, pagina blijft ongewijzigd): de
/// Blazor-pagina genereert client-side nog steeds een tekstsleutel in de vorm
/// <c>"ALLSTARS-&lt;hex&gt;"</c> (<c>BlazorAdmin/Pages/TestData/Wedstrijden.razor</c>). Op deze tier
/// is <c>his.matches.bk_matches</c> echter een <c>GENERATED ALWAYS</c>-kolom, afgeleid van het
/// numerieke <c>wedstrijdcode</c> (zie #820/#853-precedent voor synthetische sleutels op deze
/// tier) — die kolom kan dus niet rechtstreeks beschreven worden.
/// </para>
/// <para>
/// <see cref="DeriveWedstrijdcode"/> zet elke aangeboden tekstsleutel deterministisch (SHA-256, dus
/// stabiel over processen/herstarts heen — <b>nooit</b> <c>string.GetHashCode()</c>, die is sinds
/// .NET Core per proces gerandomiseerd) om in een <c>wedstrijdcode</c> in het bereik
/// <c>900.000.000+</c> — ruim buiten zowel echte Sportlink-wedstrijdcodes (zie
/// <c>FunctionApp/CLAUDE.md</c>, voorbeeld 8 cijfers) als het gezaaide demobereik
/// <c>9.000.001-9.000.224</c> (<c>scripts/migrations/003-seed-allstars-demo-matches-postgres.sql</c>).
/// Is de aangeboden sleutel al numeriek (het geval ná een paginaherlaad, wanneer de pagina de door
/// de database afgeleide <c>bk_matches</c>-waarde heeft teruggekregen), dan wordt die rechtstreeks
/// als <c>wedstrijdcode</c> gebruikt — geen dubbele afleiding.
/// </para>
/// <para>
/// <b>Optie A</b> (pagina genereert zelf een numerieke sleutel) is bewust niet gekozen: dat vraagt
/// een wijziging aan de gedeelde Blazor-pagina én aan het bestaande, live SQL Server-contract — een
/// wijziging aan de bestaande, draaiende tier om de nieuwe tier te laten werken is precies de
/// regressie die <c>docs/ARCHITECTUUR-DATABASE-TIERS.md</c> §2 en de zelftest-skill uitsluiten.
/// </para>
/// </summary>
public static class AdminTestDataFunction
{
    private const string AllstarsClubCode = "ALLSTARS";

    // #820-precedent: een deterministische, niet-procesafhankelijke hash. string.GetHashCode() is
    // sinds .NET Core per proces gerandomiseerd (beveiligingsmaatregel) en zou bij elke herstart een
    // andere wedstrijdcode voor dezelfde tekstsleutel opleveren — dan matcht een upsert na een
    // herstart de bestaande rij niet meer en ontstaat er een duplicaat i.p.v. een update.
    internal static long DeriveWedstrijdcode(string bkMatches)
    {
        if (long.TryParse(bkMatches, out var alReadyNumeric))
            return alReadyNumeric;

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(bkMatches));
        var value = BitConverter.ToUInt32(hash, 0);
        return 900_000_000L + (value % 90_000_000L);
    }

    [Function("TestDataWedstrijdenGet")]
    public static Task<IActionResult> GetWedstrijden(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/testdata/wedstrijden")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("TestDataWedstrijdenGet"), "ALLSTARS wedstrijden ophalen",
            async _ =>
            {
                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    """
                    SELECT bk_matches, datum, aanvangstijd, thuisteam, uitteam, veld, competitiesoort, veld_subpositie
                    FROM his.matches
                    WHERE clubcode = @clubcode
                    ORDER BY datum, aanvangstijd, thuisteam
                    """, connection);
                command.Parameters.AddWithValue("clubcode", AllstarsClubCode);
                await using var reader = await command.ExecuteReaderAsync();
                var list = new List<object>();
                while (await reader.ReadAsync())
                    list.Add(new
                    {
                        BkMatches = reader.GetString(0),
                        Datum = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Aanvangstijd = reader.IsDBNull(2) ? null : reader.GetString(2),
                        ThuisTeam = reader.IsDBNull(3) ? null : reader.GetString(3),
                        UitTeam = reader.IsDBNull(4) ? null : reader.GetString(4),
                        VeldNaam = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Soort = reader.IsDBNull(6) ? null : reader.GetString(6),
                        VeldSubpositie = reader.IsDBNull(7) ? null : reader.GetString(7),
                    });
                return new OkObjectResult(list);
            });

    // Teams uit avg.teambegeleiding voor ALLSTARS — zelfde bron als de SQL Server-tier.
    [Function("TestDataTeamsGet")]
    public static Task<IActionResult> GetTeams(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/testdata/teams")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("TestDataTeamsGet"), "teams ophalen voor testdata",
            async _ =>
            {
                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT DISTINCT team FROM avg.teambegeleiding WHERE team IS NOT NULL AND clubcode = @clubcode ORDER BY team",
                    connection);
                command.Parameters.AddWithValue("clubcode", AllstarsClubCode);
                await using var reader = await command.ExecuteReaderAsync();
                var teams = new List<string>();
                while (await reader.ReadAsync())
                    teams.Add(reader.GetString(0));
                return new OkObjectResult(teams);
            });

    [Function("TestDataWedstrijdUpsert")]
    public static Task<IActionResult> UpsertWedstrijd(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/testdata/wedstrijden")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("TestDataWedstrijdUpsert"), "ALLSTARS wedstrijd opslaan",
            async _ =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var dto = JsonConvert.DeserializeObject<AllstarsWedstrijdInput>(body);
                if (dto == null || string.IsNullOrWhiteSpace(dto.BkMatches))
                    return new BadRequestObjectResult(new { error = "BkMatches verplicht" });

                var wedstrijdcode = DeriveWedstrijdcode(dto.BkMatches);
                var wedstrijddatum = (!string.IsNullOrWhiteSpace(dto.Datum) && !string.IsNullOrWhiteSpace(dto.Aanvangstijd))
                    ? $"{dto.Datum}T{dto.Aanvangstijd}:00"
                    : dto.Datum;
                var kaledatum = !string.IsNullOrWhiteSpace(dto.Datum) ? $"{dto.Datum} 00:00:00.00" : null;

                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                // accommodatie moet gevuld zijn (#694-precedent, zelfde reden als de SQL Server-tier):
                // PlannerMatchRepository/PlannerMatchRepository filtert HerplanVerzoek-lookups op de
                // eigen-club-accommodatie.
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO his.matches
                        (wedstrijdcode, datum, wedstrijddatum, kaledatum, aanvangstijd,
                         thuisteam, teamnaam, uitteam, veld, veld_subpositie, competitiesoort,
                         accommodatie, clubcode, mta_inserted, mta_modified)
                    VALUES
                        (@wedstrijdcode, @datum, @wedstrijddatum, @kaledatum, @aanvangstijd,
                         @thuisteam, @thuisteam, @uitteam, @veld, @veldsubpositie, @soort,
                         (SELECT accommodatie FROM public.appsettings WHERE clubcode = @clubcode), @clubcode, NOW(), NOW())
                    ON CONFLICT (bk_matches) DO UPDATE SET
                        datum = @datum,
                        wedstrijddatum = @wedstrijddatum,
                        kaledatum = @kaledatum,
                        aanvangstijd = @aanvangstijd,
                        thuisteam = @thuisteam,
                        teamnaam = @thuisteam,
                        uitteam = @uitteam,
                        veld = @veld,
                        veld_subpositie = @veldsubpositie,
                        competitiesoort = @soort,
                        accommodatie = (SELECT accommodatie FROM public.appsettings WHERE clubcode = @clubcode),
                        mta_modified = NOW()
                    """, connection);
                command.Parameters.AddWithValue("wedstrijdcode", wedstrijdcode);
                command.Parameters.AddWithValue("datum", (object?)dto.Datum ?? DBNull.Value);
                command.Parameters.AddWithValue("wedstrijddatum", (object?)wedstrijddatum ?? DBNull.Value);
                command.Parameters.AddWithValue("kaledatum", (object?)kaledatum ?? DBNull.Value);
                command.Parameters.AddWithValue("aanvangstijd", (object?)dto.Aanvangstijd ?? DBNull.Value);
                command.Parameters.AddWithValue("thuisteam", (object?)dto.ThuisTeam ?? DBNull.Value);
                command.Parameters.AddWithValue("uitteam", (object?)dto.UitTeam ?? DBNull.Value);
                command.Parameters.AddWithValue("veld", (object?)dto.VeldNaam ?? DBNull.Value);
                command.Parameters.AddWithValue("veldsubpositie", (object?)dto.VeldSubpositie ?? DBNull.Value);
                command.Parameters.AddWithValue("soort", (object?)dto.Soort ?? DBNull.Value);
                command.Parameters.AddWithValue("clubcode", AllstarsClubCode);

                await command.ExecuteNonQueryAsync();
                return new OkObjectResult(new { ok = true });
            });

    [Function("TestDataVerplaatsDatum")]
    public static Task<IActionResult> VerplaatsDatum(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/testdata/wedstrijden/verplaats-datum")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("TestDataVerplaatsDatum"), "ALLSTARS wedstrijden verplaatsen",
            async _ =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var dto = JsonConvert.DeserializeObject<VerplaatsDatumInput>(body);
                if (dto == null || string.IsNullOrWhiteSpace(dto.OudeDatum) || string.IsNullOrWhiteSpace(dto.NieuweDatum))
                    return new BadRequestObjectResult(new { error = "OudeDatum en NieuweDatum zijn verplicht" });

                var nieuweKaledatum = $"{dto.NieuweDatum} 00:00:00.00";

                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    """
                    UPDATE his.matches
                    SET
                        datum = @nieuwedatum,
                        kaledatum = @nieuwekaledatum,
                        wedstrijddatum = CASE
                            WHEN wedstrijddatum IS NOT NULL AND LENGTH(wedstrijddatum) >= 10
                                THEN @nieuwedatum || SUBSTRING(wedstrijddatum FROM 11)
                            ELSE @nieuwedatum
                        END,
                        mta_modified = NOW()
                    WHERE clubcode = @clubcode
                      AND datum = @oudedatum
                    """, connection);
                command.Parameters.AddWithValue("oudedatum", dto.OudeDatum);
                command.Parameters.AddWithValue("nieuwedatum", dto.NieuweDatum);
                command.Parameters.AddWithValue("nieuwekaledatum", nieuweKaledatum);
                command.Parameters.AddWithValue("clubcode", AllstarsClubCode);
                var count = await command.ExecuteNonQueryAsync();
                return new OkObjectResult(new { ok = true, aantalVerplaatst = count });
            });

    [Function("TestDataWedstrijdDeleteEen")]
    public static Task<IActionResult> DeleteEen(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/testdata/wedstrijden/{bk}")] HttpRequest req,
        string bk,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("TestDataWedstrijdDeleteEen"), "ALLSTARS wedstrijd verwijderen",
            async _ =>
            {
                var wedstrijdcode = DeriveWedstrijdcode(bk);
                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "DELETE FROM his.matches WHERE wedstrijdcode = @wedstrijdcode AND clubcode = @clubcode",
                    connection);
                command.Parameters.AddWithValue("wedstrijdcode", wedstrijdcode);
                command.Parameters.AddWithValue("clubcode", AllstarsClubCode);
                await command.ExecuteNonQueryAsync();
                return new OkObjectResult(new { ok = true });
            });

    [Function("TestDataWedstrijdenDeleteAlle")]
    public static Task<IActionResult> DeleteAlle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/testdata/wedstrijden")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("TestDataWedstrijdenDeleteAlle"), "ALLSTARS wedstrijden verwijderen",
            async _ =>
            {
                var vanStr = req.Query.ContainsKey("van") ? req.Query["van"].ToString() : null;
                var totStr = req.Query.ContainsKey("tot") ? req.Query["tot"].ToString() : null;

                var sql = "DELETE FROM his.matches WHERE clubcode = @clubcode";
                if (!string.IsNullOrEmpty(vanStr)) sql += " AND datum >= @van";
                if (!string.IsNullOrEmpty(totStr)) sql += " AND datum <= @tot";

                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("clubcode", AllstarsClubCode);
                if (!string.IsNullOrEmpty(vanStr)) command.Parameters.AddWithValue("van", vanStr);
                if (!string.IsNullOrEmpty(totStr)) command.Parameters.AddWithValue("tot", totStr);
                await command.ExecuteNonQueryAsync();
                return new OkObjectResult(new { ok = true });
            });

    private sealed class AllstarsWedstrijdInput
    {
        public string BkMatches { get; set; } = "";
        public string? Datum { get; set; }
        public string? Aanvangstijd { get; set; }
        public string? ThuisTeam { get; set; }
        public string? UitTeam { get; set; }
        public string? VeldNaam { get; set; }
        public string? VeldSubpositie { get; set; }
        public string? Soort { get; set; }
    }

    private sealed class VerplaatsDatumInput
    {
        public string? OudeDatum { get; set; }
        public string? NieuweDatum { get; set; }
    }
}
