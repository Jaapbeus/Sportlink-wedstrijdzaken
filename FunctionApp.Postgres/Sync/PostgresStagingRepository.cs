using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres.Sync;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Sync/SportlinkStagingRepository.cs</c> (#890) —
/// schrijft opgehaalde Sportlink-API-data naar <c>stg.teams</c>/<c>stg.matches</c>/
/// <c>stg.matchdetails</c>, de tabellen die <see cref="Database.Postgres.PostgresMergeOrchestrator"/>
/// per sync-run herbouwt (<c>RecreateStgTableAsync</c>).
/// <para>
/// <b>Vertaalbeslissingen tegenover de SQL Server-versie:</b>
/// </para>
/// <list type="bullet">
/// <item><c>uitslag-regulier</c>/<c>uitslag-nv</c>/<c>uitslag-s</c> bevatten een koppelteken —
/// <see cref="Database.Postgres.PostgresIdentifier.Quote"/> quote't ze bij het aanmaken, dus elke
/// referentie hier moet ze ook expliciet gequote gebruiken.</item>
/// <item>De "insert alleen als nog niet aanwezig"-dedupguard (programma) en de
/// "update-als-bestaat-anders-conditioneel-insert"-guard (uitslagen) uit de SQL Server-versie zijn
/// hier vertaald met expliciete <c>SELECT</c>/<c>UPDATE</c>/<c>INSERT</c>-stappen in C# in plaats
/// van SQL Server's <c>IF EXISTS ... ELSE IF ...</c>-syntax (die Postgres niet kent als top-level
/// statement). De datumvergelijking (nooit een toekomstige wedstrijd via /uitslagen laten
/// binnenkomen) gebeurt daarom als een ordinale stringvergelijking in C# tegen een UTC-ISO8601-
/// tijdstip, functioneel gelijk aan het origineel se <c>CONVERT(NVARCHAR(50), GETUTCDATE(), 127)</c>.</item>
/// </list>
/// </summary>
internal static class PostgresStagingRepository
{
    internal static async Task<List<string>> GetWedstrijdcodesAsync(string connectionString, ILogger log)
    {
        var list = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT wedstrijdcode FROM stg.matches", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(reader.GetValue(0).ToString() ?? string.Empty);
        return list;
    }

    internal static async Task SaveTeamsAsync(string connectionString, List<Team> teams, string clubCode, ILogger log)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var team in teams)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO stg.teams
                    (teamcode, lokaleteamcode, poulecode, teamnaam, competitienaam, klasse,
                     poule, klassepoule, spelsoort, competitiesoort, geslacht, teamsoort,
                     leeftijdscategorie, kalespelsoort, speeldag, speeldagteam, more, clubcode)
                VALUES
                    (@teamcode, @lokaleteamcode, @poulecode, @teamnaam, @competitienaam, @klasse,
                     @poule, @klassepoule, @spelsoort, @competitiesoort, @geslacht, @teamsoort,
                     @leeftijdscategorie, @kalespelsoort, @speeldag, @speeldagteam, @more, @clubcode)
                """, connection);
            command.Parameters.AddWithValue("clubcode", clubCode);
            command.Parameters.AddWithValue("teamcode", team.teamcode);
            command.Parameters.AddWithValue("lokaleteamcode", team.lokaleteamcode);
            command.Parameters.AddWithValue("poulecode", (object?)team.poulecode ?? DBNull.Value);
            command.Parameters.AddWithValue("teamnaam", team.teamnaam);
            command.Parameters.AddWithValue("competitienaam", team.competitienaam);
            command.Parameters.AddWithValue("klasse", team.klasse);
            command.Parameters.AddWithValue("poule", team.poule);
            command.Parameters.AddWithValue("klassepoule", team.klassepoule);
            command.Parameters.AddWithValue("spelsoort", team.spelsoort);
            command.Parameters.AddWithValue("competitiesoort", team.competitiesoort);
            command.Parameters.AddWithValue("geslacht", team.geslacht);
            command.Parameters.AddWithValue("teamsoort", team.teamsoort);
            command.Parameters.AddWithValue("leeftijdscategorie", team.leeftijdscategorie);
            command.Parameters.AddWithValue("kalespelsoort", team.kalespelsoort);
            command.Parameters.AddWithValue("speeldag", team.speeldag);
            command.Parameters.AddWithValue("speeldagteam", team.speeldagteam);
            command.Parameters.AddWithValue("more", team.more);
            await command.ExecuteNonQueryAsync();
        }
        log.LogInformation("TEAMS - {Count} rows inserted into staging.", teams.Count);
    }

    internal static async Task<int> SaveProgrammaAsync(string connectionString, List<Match> matches, string clubCode, ILogger log)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var inserted = 0;
        foreach (var match in matches)
        {
            await using var existsCommand = new NpgsqlCommand(
                "SELECT 1 FROM stg.matches WHERE wedstrijdcode = @wedstrijdcode", connection);
            existsCommand.Parameters.AddWithValue("wedstrijdcode", match.wedstrijdcode);
            if (await existsCommand.ExecuteScalarAsync() != null)
                continue;

            await using var insertCommand = new NpgsqlCommand("""
                INSERT INTO stg.matches (
                     wedstrijddatum, wedstrijdcode, wedstrijdnummer, datum, wedstrijd
                    ,accommodatie, aanvangstijd, thuisteam, thuisteamid, thuisteamlogo
                    ,thuisteamclubrelatiecode, uitteamclubrelatiecode, uitteam, uitteamid
                    ,uitteamlogo, competitiesoort, status, meer
                    ,teamnaam, teamvolgorde, competitie, klasse, poule, klassepoule
                    ,kaledatum, vertrektijd, verzameltijd, scheidsrechters, scheidsrechter
                    ,veld, locatie, plaats, rijders
                    ,kleedkamerthuisteam, kleedkameruitteam, kleedkamerscheidsrechter
                    ,clubcode
                ) VALUES (
                     @wedstrijddatum, @wedstrijdcode, @wedstrijdnummer, @datum, @wedstrijd
                    ,@accommodatie, @aanvangstijd, @thuisteam, @thuisteamid, @thuisteamlogo
                    ,@thuisteamclubrelatiecode, @uitteamclubrelatiecode, @uitteam, @uitteamid
                    ,@uitteamlogo, @competitiesoort, @status, @meer
                    ,@teamnaam, @teamvolgorde, @competitie, @klasse, @poule, @klassepoule
                    ,@kaledatum, @vertrektijd, @verzameltijd, @scheidsrechters, @scheidsrechter
                    ,@veld, @locatie, @plaats, @rijders
                    ,@kleedkamerthuisteam, @kleedkameruitteam, @kleedkamerscheidsrechter
                    ,@clubcode
                )
                """, connection);
            AddMatchParams(insertCommand, match);
            insertCommand.Parameters.AddWithValue("clubcode", clubCode);
            insertCommand.Parameters.AddWithValue("teamnaam", match.teamnaam);
            insertCommand.Parameters.AddWithValue("teamvolgorde", match.teamvolgorde);
            insertCommand.Parameters.AddWithValue("competitie", match.competitie);
            insertCommand.Parameters.AddWithValue("klasse", match.klasse);
            insertCommand.Parameters.AddWithValue("poule", match.poule);
            insertCommand.Parameters.AddWithValue("klassepoule", match.klassepoule);
            insertCommand.Parameters.AddWithValue("kaledatum", match.kaledatum);
            insertCommand.Parameters.AddWithValue("vertrektijd", match.vertrektijd);
            insertCommand.Parameters.AddWithValue("verzameltijd", match.verzameltijd);
            insertCommand.Parameters.AddWithValue("scheidsrechters", match.scheidsrechters);
            insertCommand.Parameters.AddWithValue("scheidsrechter", match.scheidsrechter);
            insertCommand.Parameters.AddWithValue("veld", match.veld);
            insertCommand.Parameters.AddWithValue("locatie", match.locatie);
            insertCommand.Parameters.AddWithValue("plaats", match.plaats);
            insertCommand.Parameters.AddWithValue("rijders", match.rijders);
            insertCommand.Parameters.AddWithValue("kleedkamerthuisteam", match.kleedkamerthuisteam);
            insertCommand.Parameters.AddWithValue("kleedkameruitteam", match.kleedkameruitteam);
            insertCommand.Parameters.AddWithValue("kleedkamerscheidsrechter", match.kleedkamerscheidsrechter);
            if (await insertCommand.ExecuteNonQueryAsync() > 0) inserted++;
        }
        log.LogInformation("MATCHES/PROGRAMMA - {Inserted} new rows inserted into staging.", inserted);
        return inserted;
    }

    internal static async Task<int> MergeUitslagenAsync(string connectionString, List<Match> matches, string clubCode, ILogger log)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var updated = 0;
        // Zelfde vergelijkingsvorm als het origineel (CONVERT(..., 127) — ISO8601), zodat een
        // ordinale stringvergelijking tegen wedstrijddatum functioneel identiek blijft.
        var nowUtcIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");

        foreach (var match in matches)
        {
            await using var updateCommand = new NpgsqlCommand("""
                UPDATE stg.matches SET
                     uitslag              = @uitslag
                    ,"uitslag-regulier"    = @uitslagregulier
                    ,"uitslag-nv"          = @uitslagnv
                    ,"uitslag-s"           = @uitslags
                    ,datumopgemaakt        = @datumopgemaakt
                    ,competitienaam        = @competitienaam
                    ,eigenteam             = @eigenteam
                    ,sportomschrijving     = @sportomschrijving
                    ,verenigingswedstrijd  = @verenigingswedstrijd
                    ,status                = @status
                WHERE wedstrijdcode = @wedstrijdcode
                """, connection);
            AddMatchParams(updateCommand, match);
            AddUitslagenParams(updateCommand, match);
            var rowsUpdated = await updateCommand.ExecuteNonQueryAsync();
            if (rowsUpdated > 0)
            {
                updated++;
                continue;
            }

            // Nog geen rij aanwezig — alleen invoegen als de wedstrijd niet in de toekomst ligt
            // (voorkomt dat /uitslagen ooit een nog te spelen wedstrijd zou "voorspellen").
            if (string.CompareOrdinal(match.wedstrijddatum, nowUtcIso) > 0)
                continue;

            await using var insertCommand = new NpgsqlCommand("""
                INSERT INTO stg.matches (
                     wedstrijddatum, wedstrijdcode, wedstrijdnummer, datum, wedstrijd
                    ,accommodatie, aanvangstijd, thuisteam, thuisteamid, thuisteamlogo
                    ,thuisteamclubrelatiecode, uitteamclubrelatiecode, uitteam, uitteamid
                    ,uitteamlogo, competitiesoort, status, meer
                    ,datumopgemaakt, uitslag, "uitslag-regulier", "uitslag-nv", "uitslag-s"
                    ,competitienaam, eigenteam, sportomschrijving, verenigingswedstrijd
                    ,clubcode
                ) VALUES (
                     @wedstrijddatum, @wedstrijdcode, @wedstrijdnummer, @datum, @wedstrijd
                    ,@accommodatie, @aanvangstijd, @thuisteam, @thuisteamid, @thuisteamlogo
                    ,@thuisteamclubrelatiecode, @uitteamclubrelatiecode, @uitteam, @uitteamid
                    ,@uitteamlogo, @competitiesoort, @status, @meer
                    ,@datumopgemaakt, @uitslag, @uitslagregulier, @uitslagnv, @uitslags
                    ,@competitienaam, @eigenteam, @sportomschrijving, @verenigingswedstrijd
                    ,@clubcode
                )
                """, connection);
            AddMatchParams(insertCommand, match);
            AddUitslagenParams(insertCommand, match);
            if (await insertCommand.ExecuteNonQueryAsync() > 0) updated++;
        }
        log.LogInformation("MATCHES/UITSLAGEN - {Updated} rows merged (updated or inserted) into staging.", updated);
        return updated;
    }

    internal static async Task SaveMatchDetailsAsync(string connectionString, MatchDetails matchDetails, string clubCode, ILogger log)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var existsCommand = new NpgsqlCommand(
            "SELECT 1 FROM stg.matchdetails WHERE wedstrijdcode = @wedstrijdcode", connection);
        existsCommand.Parameters.AddWithValue("wedstrijdcode", matchDetails.Wedstrijdinformatie.Wedstrijdnummer);
        if (await existsCommand.ExecuteScalarAsync() != null)
        {
            log.LogInformation("MATCHDETAILS - stg.matchdetails rij bestaat al, overgeslagen.");
            return;
        }

        await using var command = new NpgsqlCommand("""
            INSERT INTO stg.matchdetails (
                wedstrijdcode, interncode, veldnaam, veldlocatie, vertrektijd, rijder,
                thuisscore, thuisscoreregulier, thuisscorenv, thuisscores, uitscore, uitscoreregulier,
                uitscorenv, uitscores, klasse, wedstrijdtype, competitietype, categorie, matchdatetime,
                matchdate, aanvangstijd, duration, speltype, aanduiding, poulecode, poule, thuisteamid,
                thuisteam, uitteamid, uitteam, opmerkingen, verenigingscheidsrechtercode, verenigingscheidsrechter,
                overigeofficialcode, overigeofficial, scheidsrechters, kleedkamerthuis, kleedkameruit, kleedkamerofficial,
                accommodatienaam, accommodatiestraat, accommodatieplaats, accommodatietelefoon, accommodatierouteplanner,
                thuisteamnaam, thuisteamcode, thuisteamwebsite, thuisteamshirtkleur, thuisteamstraat,
                thuisteampostcodeplaats, thuisteamtelefoon, thuisteamemail, uitteamnaam, uitteamcode,
                uitteamwebsite, uitteamshirtkleur, uitteamstraat, uitteampostcodeplaats, uitteamtelefoon, uitteamemail,
                clubcode
            ) VALUES (
                @wedstrijdcode, @interncode, @veldnaam, @veldlocatie, @vertrektijd, @rijder,
                @thuisscore, @thuisscoreregulier, @thuisscorenv, @thuisscores, @uitscore, @uitscoreregulier,
                @uitscorenv, @uitscores, @klasse, @wedstrijdtype, @competitietype, @categorie, @matchdatetime,
                @matchdate, @aanvangstijd, @duration, @speltype, @aanduiding, @poulecode, @poule, @thuisteamid,
                @thuisteam, @uitteamid, @uitteam, @opmerkingen, @verenigingscheidsrechtercode, @verenigingscheidsrechter,
                @overigeofficialcode, @overigeofficial, @scheidsrechters, @kleedkamerthuis, @kleedkameruit, @kleedkamerofficial,
                @accommodatienaam, @accommodatiestraat, @accommodatieplaats, @accommodatietelefoon, @accommodatierouteplanner,
                @thuisteamnaam, @thuisteamcode, @thuisteamwebsite, @thuisteamshirtkleur, @thuisteamstraat,
                @thuisteampostcodeplaats, @thuisteamtelefoon, @thuisteamemail, @uitteamnaam, @uitteamcode,
                @uitteamwebsite, @uitteamshirtkleur, @uitteamstraat, @uitteampostcodeplaats, @uitteamtelefoon, @uitteamemail,
                @clubcode
            )
            """, connection);
        command.Parameters.AddWithValue("clubcode", clubCode);

        var wi = matchDetails.Wedstrijdinformatie;
        command.Parameters.AddWithValue("wedstrijdcode", wi.Wedstrijdnummer);
        command.Parameters.AddWithValue("interncode", wi.Wedstijdnummerintern);
        command.Parameters.AddWithValue("veldnaam", wi.Veldnaam);
        command.Parameters.AddWithValue("veldlocatie", wi.Veldlocatie);
        command.Parameters.AddWithValue("vertrektijd", wi.Vertrektijd);
        command.Parameters.AddWithValue("rijder", wi.Rijder);
        command.Parameters.AddWithValue("thuisscore", wi.Thuisscore);
        command.Parameters.AddWithValue("thuisscoreregulier", wi.ThuisscoreRegulier);
        command.Parameters.AddWithValue("thuisscorenv", wi.ThuisscoreNv);
        command.Parameters.AddWithValue("thuisscores", wi.ThuisscoreS);
        command.Parameters.AddWithValue("uitscore", wi.Uitscore);
        command.Parameters.AddWithValue("uitscoreregulier", wi.UitscoreRegulier);
        command.Parameters.AddWithValue("uitscorenv", wi.UitscoreNv);
        command.Parameters.AddWithValue("uitscores", wi.UitscoreS);
        command.Parameters.AddWithValue("klasse", wi.Klasse);
        command.Parameters.AddWithValue("wedstrijdtype", wi.Wedstrijdtype);
        command.Parameters.AddWithValue("competitietype", wi.Competitietype);
        command.Parameters.AddWithValue("categorie", wi.Categorie);
        command.Parameters.AddWithValue("matchdatetime", (object?)wi.Wedstrijddatetime ?? DBNull.Value);
        command.Parameters.AddWithValue("matchdate",
            wi.Wedstrijddatum.HasValue ? DateOnly.FromDateTime(wi.Wedstrijddatum.Value) : (object)DBNull.Value);
        command.Parameters.AddWithValue("aanvangstijd",
            TimeSpan.TryParse(wi.Aanvangstijd, out var ts) ? ts : (object)DBNull.Value);
        command.Parameters.AddWithValue("duration", (object?)wi.Duur ?? DBNull.Value);
        command.Parameters.AddWithValue("speltype", wi.Speltype);
        command.Parameters.AddWithValue("aanduiding", wi.Aanduiding);
        command.Parameters.AddWithValue("poulecode", int.TryParse(wi.Poulecode, out var pc) ? pc : (object)DBNull.Value);
        command.Parameters.AddWithValue("poule", wi.Poule);
        command.Parameters.AddWithValue("thuisteamid", wi.Thuisteamid);
        command.Parameters.AddWithValue("thuisteam", wi.Thuisteam);
        command.Parameters.AddWithValue("uitteamid", wi.Uitteamid);
        command.Parameters.AddWithValue("uitteam", wi.Uitteam);
        command.Parameters.AddWithValue("opmerkingen", wi.Opmerkingen);
        command.Parameters.AddWithValue("verenigingscheidsrechtercode", matchDetails.Officials.Verenigingsscheidsrechtercode);
        command.Parameters.AddWithValue("verenigingscheidsrechter", matchDetails.Officials.Verenigingsscheidsrechter);
        command.Parameters.AddWithValue("overigeofficialcode", matchDetails.Officials.Overigeofficialcode);
        command.Parameters.AddWithValue("overigeofficial", matchDetails.Officials.Overigeofficial);
        command.Parameters.AddWithValue("scheidsrechters", matchDetails.Matchofficials.Scheidsrechters);
        command.Parameters.AddWithValue("kleedkamerthuis", matchDetails.Kleedkamers.Thuis);
        command.Parameters.AddWithValue("kleedkameruit", matchDetails.Kleedkamers.Uit);
        command.Parameters.AddWithValue("kleedkamerofficial", matchDetails.Kleedkamers.Official);
        command.Parameters.AddWithValue("accommodatienaam", matchDetails.Accommodatie.Naam);
        command.Parameters.AddWithValue("accommodatiestraat", matchDetails.Accommodatie.Straat);
        command.Parameters.AddWithValue("accommodatieplaats", matchDetails.Accommodatie.Plaats);
        command.Parameters.AddWithValue("accommodatietelefoon", matchDetails.Accommodatie.Telefoon);
        command.Parameters.AddWithValue("accommodatierouteplanner", matchDetails.Accommodatie.Routeplanner);
        command.Parameters.AddWithValue("thuisteamnaam", matchDetails.Thuisteam.Naam);
        command.Parameters.AddWithValue("thuisteamcode", matchDetails.Thuisteam.Code);
        command.Parameters.AddWithValue("thuisteamwebsite", matchDetails.Thuisteam.Website);
        command.Parameters.AddWithValue("thuisteamshirtkleur", matchDetails.Thuisteam.Shirtkleur);
        command.Parameters.AddWithValue("thuisteamstraat", matchDetails.Thuisteam.Straat);
        command.Parameters.AddWithValue("thuisteampostcodeplaats", matchDetails.Thuisteam.Postcodeplaats);
        command.Parameters.AddWithValue("thuisteamtelefoon", matchDetails.Thuisteam.Telefoon);
        command.Parameters.AddWithValue("thuisteamemail", matchDetails.Thuisteam.Email);
        command.Parameters.AddWithValue("uitteamnaam", matchDetails.Uitteam.Naam);
        command.Parameters.AddWithValue("uitteamcode", matchDetails.Uitteam.Code);
        command.Parameters.AddWithValue("uitteamwebsite", matchDetails.Uitteam.Website);
        command.Parameters.AddWithValue("uitteamshirtkleur", matchDetails.Uitteam.Shirtkleur);
        command.Parameters.AddWithValue("uitteamstraat", matchDetails.Uitteam.Straat);
        command.Parameters.AddWithValue("uitteampostcodeplaats", matchDetails.Uitteam.Postcodeplaats);
        command.Parameters.AddWithValue("uitteamtelefoon", matchDetails.Uitteam.Telefoon);
        command.Parameters.AddWithValue("uitteamemail", matchDetails.Uitteam.Email);
        await command.ExecuteNonQueryAsync();
        log.LogInformation("MATCHDETAILS - stg.matchdetails rij opgeslagen.");
    }

    // Gedeelde basisvelden voor programma én uitslagen.
    private static void AddMatchParams(NpgsqlCommand command, Match match)
    {
        command.Parameters.AddWithValue("wedstrijddatum", match.wedstrijddatum);
        command.Parameters.AddWithValue("wedstrijdcode", match.wedstrijdcode);
        command.Parameters.AddWithValue("wedstrijdnummer", match.wedstrijdnummer);
        command.Parameters.AddWithValue("datum", match.datum);
        command.Parameters.AddWithValue("wedstrijd", match.wedstrijd);
        command.Parameters.AddWithValue("accommodatie", match.accommodatie);
        command.Parameters.AddWithValue("aanvangstijd", match.aanvangstijd);
        command.Parameters.AddWithValue("thuisteam", match.thuisteam);
        command.Parameters.AddWithValue("thuisteamid", match.thuisteamid);
        command.Parameters.AddWithValue("thuisteamlogo", match.thuisteamlogo);
        command.Parameters.AddWithValue("thuisteamclubrelatiecode", match.thuisteamclubrelatiecode);
        command.Parameters.AddWithValue("uitteamclubrelatiecode", match.uitteamclubrelatiecode);
        command.Parameters.AddWithValue("uitteam", match.uitteam);
        command.Parameters.AddWithValue("uitteamid", match.uitteamid);
        command.Parameters.AddWithValue("uitteamlogo", match.uitteamlogo);
        command.Parameters.AddWithValue("competitiesoort", match.competitiesoort);
        command.Parameters.AddWithValue("status", match.status);
        command.Parameters.AddWithValue("meer", match.meer);
    }

    private static void AddUitslagenParams(NpgsqlCommand command, Match match)
    {
        command.Parameters.AddWithValue("datumopgemaakt", match.datumopgemaakt);
        command.Parameters.AddWithValue("uitslag", match.uitslag);
        command.Parameters.AddWithValue("uitslagregulier", match.uitslag_regulier);
        command.Parameters.AddWithValue("uitslagnv", match.uitslag_nv);
        command.Parameters.AddWithValue("uitslags", match.uitslag_s);
        command.Parameters.AddWithValue("competitienaam", match.competitienaam);
        command.Parameters.AddWithValue("eigenteam", match.eigenteam);
        command.Parameters.AddWithValue("sportomschrijving", match.sportomschrijving);
        command.Parameters.AddWithValue("verenigingswedstrijd", match.verenigingswedstrijd);
    }
}
