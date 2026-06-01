using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using static SportlinkFunction.SystemUtilities;

namespace SportlinkFunction;

/// <summary>
/// Staging-laag voor Sportlink-data: schrijft opgehaalde API-data naar stg.*-tabellen.
/// Extracted uit Function1.cs (#466).
/// </summary>
internal static class SportlinkStagingRepository
{
    private static string Cs => DatabaseConfig.ConnectionString;

    internal static async Task<List<string>> GetWedstrijdcodesAsync(ILogger log)
    {
        var list = new List<string>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("SELECT wedstrijdcode FROM [stg].[matches]", conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var code = reader["wedstrijdcode"]?.ToString();
            if (code != null) list.Add(code);
        }
        return list;
    }

    internal static async Task SaveTeamsAsync(List<Team> teams, ILogger log)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        foreach (var team in teams)
        {
            using var cmd = new SqlCommand(@"
                INSERT INTO [stg].[teams]
                    ([teamcode],[lokaleteamcode],[poulecode],[teamnaam],[competitienaam],[klasse],
                     [poule],[klassepoule],[spelsoort],[competitiesoort],[geslacht],[teamsoort],
                     [leeftijdscategorie],[kalespelsoort],[speeldag],[speeldagteam],[more])
                VALUES
                    (@teamcode,@lokaleteamcode,@poulecode,@teamnaam,@competitienaam,@klasse,
                     @poule,@klassepoule,@spelsoort,@competitiesoort,@geslacht,@teamsoort,
                     @leeftijdscategorie,@kalespelsoort,@speeldag,@speeldagteam,@more)", conn);
            cmd.Parameters.AddWithValue("@teamcode",          team.teamcode);
            cmd.Parameters.AddWithValue("@lokaleteamcode",    team.lokaleteamcode);
            cmd.Parameters.AddWithValue("@poulecode",         team.poulecode         ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@teamnaam",          team.teamnaam);
            cmd.Parameters.AddWithValue("@competitienaam",    team.competitienaam    ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@klasse",            team.klasse            ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@poule",             team.poule             ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@klassepoule",       team.klassepoule       ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@spelsoort",         team.spelsoort         ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@competitiesoort",   team.competitiesoort   ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@geslacht",          team.geslacht          ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@teamsoort",         team.teamsoort         ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@leeftijdscategorie",team.leeftijdscategorie?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@kalespelsoort",     team.kalespelsoort     ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@speeldag",          team.speeldag          ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@speeldagteam",      team.speeldagteam      ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@more",              team.more              ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        log.LogInformation("TEAMS - {Count} rows inserted into staging.", teams.Count);
    }

    internal static async Task<int> SaveProgrammaAsync(List<Match> matches, ILogger log)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        int inserted = 0;
        foreach (var match in matches)
        {
            using var cmd = new SqlCommand(@"
                IF NOT EXISTS (SELECT 1 FROM [stg].[matches] WHERE [wedstrijdcode] = @wedstrijdcode)
                INSERT INTO [stg].[matches] (
                     [wedstrijddatum],[wedstrijdcode],[wedstrijdnummer],[datum],[wedstrijd]
                    ,[accommodatie],[aanvangstijd],[thuisteam],[thuisteamid],[thuisteamlogo]
                    ,[thuisteamclubrelatiecode],[uitteamclubrelatiecode],[uitteam],[uitteamid]
                    ,[uitteamlogo],[competitiesoort],[status],[meer]
                    ,[teamnaam],[teamvolgorde],[competitie],[klasse],[poule],[klassepoule]
                    ,[kaledatum],[vertrektijd],[verzameltijd],[scheidsrechters],[scheidsrechter]
                    ,[veld],[locatie],[plaats],[rijders]
                    ,[kleedkamerthuisteam],[kleedkameruitteam],[kleedkamerscheidsrechter]
                ) VALUES (
                     @wedstrijddatum,@wedstrijdcode,@wedstrijdnummer,@datum,@wedstrijd
                    ,@accommodatie,@aanvangstijd,@thuisteam,@thuisteamid,@thuisteamlogo
                    ,@thuisteamclubrelatiecode,@uitteamclubrelatiecode,@uitteam,@uitteamid
                    ,@uitteamlogo,@competitiesoort,@status,@meer
                    ,@teamnaam,@teamvolgorde,@competitie,@klasse,@poule,@klassepoule
                    ,@kaledatum,@vertrektijd,@verzameltijd,@scheidsrechters,@scheidsrechter
                    ,@veld,@locatie,@plaats,@rijders
                    ,@kleedkamerthuisteam,@kleedkameruitteam,@kleedkamerscheidsrechter
                )", conn);
            AddMatchParams(cmd, match);
            cmd.Parameters.AddWithValue("@teamnaam",                   match.teamnaam                  ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@teamvolgorde",               match.teamvolgorde);
            cmd.Parameters.AddWithValue("@competitie",                 match.competitie                ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@klasse",                     match.klasse                    ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@poule",                      match.poule                     ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@klassepoule",                match.klassepoule               ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@kaledatum",                  match.kaledatum                 ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@vertrektijd",                match.vertrektijd               ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@verzameltijd",               match.verzameltijd              ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@scheidsrechters",            match.scheidsrechters           ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@scheidsrechter",             match.scheidsrechter            ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@veld",                       match.veld                      ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@locatie",                    match.locatie                   ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@plaats",                     match.plaats                    ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@rijders",                    match.rijders                   ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@kleedkamerthuisteam",        match.kleedkamerthuisteam       ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@kleedkameruitteam",          match.kleedkameruitteam         ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@kleedkamerscheidsrechter",   match.kleedkamerscheidsrechter  ?? (object)DBNull.Value);
            if (await cmd.ExecuteNonQueryAsync() > 0) inserted++;
        }
        log.LogInformation("MATCHES/PROGRAMMA - {Inserted} new rows inserted into staging.", inserted);
        return inserted;
    }

    internal static async Task<int> MergeUitslagenAsync(List<Match> matches, ILogger log)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        int updated = 0;
        foreach (var match in matches)
        {
            using var cmd = new SqlCommand(@"
                IF EXISTS (SELECT 1 FROM [stg].[matches] WHERE [wedstrijdcode] = @wedstrijdcode)
                    UPDATE [stg].[matches] SET
                         [uitslag]              = @uitslag
                        ,[uitslag-regulier]     = @uitslagregulier
                        ,[uitslag-nv]           = @uitslagnv
                        ,[uitslag-s]            = @uitslags
                        ,[datumopgemaakt]       = @datumopgemaakt
                        ,[competitienaam]       = @competitienaam
                        ,[eigenteam]            = @eigenteam
                        ,[sportomschrijving]    = @sportomschrijving
                        ,[verenigingswedstrijd] = @verenigingswedstrijd
                        ,[status]               = @status
                    WHERE [wedstrijdcode] = @wedstrijdcode
                ELSE IF @wedstrijddatum <= CONVERT(NVARCHAR(50), GETUTCDATE(), 127)
                    INSERT INTO [stg].[matches] (
                         [wedstrijddatum],[wedstrijdcode],[wedstrijdnummer],[datum],[wedstrijd]
                        ,[accommodatie],[aanvangstijd],[thuisteam],[thuisteamid],[thuisteamlogo]
                        ,[thuisteamclubrelatiecode],[uitteamclubrelatiecode],[uitteam],[uitteamid]
                        ,[uitteamlogo],[competitiesoort],[status],[meer]
                        ,[datumopgemaakt],[uitslag],[uitslag-regulier],[uitslag-nv],[uitslag-s]
                        ,[competitienaam],[eigenteam],[sportomschrijving],[verenigingswedstrijd]
                    ) VALUES (
                         @wedstrijddatum,@wedstrijdcode,@wedstrijdnummer,@datum,@wedstrijd
                        ,@accommodatie,@aanvangstijd,@thuisteam,@thuisteamid,@thuisteamlogo
                        ,@thuisteamclubrelatiecode,@uitteamclubrelatiecode,@uitteam,@uitteamid
                        ,@uitteamlogo,@competitiesoort,@status,@meer
                        ,@datumopgemaakt,@uitslag,@uitslagregulier,@uitslagnv,@uitslags
                        ,@competitienaam,@eigenteam,@sportomschrijving,@verenigingswedstrijd
                    )", conn);
            AddMatchParams(cmd, match);
            cmd.Parameters.AddWithValue("@datumopgemaakt",      match.datumopgemaakt     ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@uitslag",             match.uitslag            ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@uitslagregulier",     match.uitslag_regulier   ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@uitslagnv",           match.uitslag_nv         ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@uitslags",            match.uitslag_s          ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@competitienaam",      match.competitienaam     ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@eigenteam",           match.eigenteam          ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@sportomschrijving",   match.sportomschrijving  ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@verenigingswedstrijd",match.verenigingswedstrijd ?? (object)DBNull.Value);
            if (await cmd.ExecuteNonQueryAsync() > 0) updated++;
        }
        log.LogInformation("MATCHES/UITSLAGEN - {Updated} rows merged (updated or inserted) into staging.", updated);
        return updated;
    }

    internal static async Task SaveMatchDetailsAsync(MatchDetails matchDetails, ILogger log)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            IF NOT EXISTS (SELECT 1 FROM [stg].[matchdetails] WHERE WedstrijdCode = @WedstrijdCode)
            INSERT INTO [stg].[matchdetails] (
                WedstrijdCode, InternCode, VeldNaam, VeldLocatie, VertrekTijd, Rijder,
                ThuisScore, ThuisScoreRegulier, ThuisScoreNV, ThuisScoreS, UitScore, UitScoreRegulier,
                UitScoreNV, UitScoreS, Klasse, WedstrijdType, CompetitieType, Categorie, MatchDateTime,
                MatchDate, Aanvangstijd, Duration, SpelType, Aanduiding, PouleCode, Poule, ThuisTeamID,
                ThuisTeam, UitTeamID, UitTeam, Opmerkingen, VerenigingScheidsrechterCode, VerenigingScheidsrechter,
                OverigeOfficialCode, OverigeOfficial, Scheidsrechters, KleedkamerThuis, KleedkamerUit, KleedkamerOfficial,
                AccommodatieNaam, AccommodatieStraat, AccommodatiePlaats, AccommodatieTelefoon, AccommodatieRouteplanner,
                ThuisTeamNaam, ThuisTeamCode, ThuisTeamWebsite, ThuisTeamShirtKleur, ThuisTeamStraat,
                ThuisTeamPostcodePlaats, ThuisTeamTelefoon, ThuisTeamEmail, UitTeamNaam, UitTeamCode,
                UitTeamWebsite, UitTeamShirtKleur, UitTeamStraat, UitTeamPostcodePlaats, UitTeamTelefoon, UitTeamEmail
            ) VALUES (
                @WedstrijdCode, @InternCode, @VeldNaam, @VeldLocatie, @VertrekTijd, @Rijder,
                @ThuisScore, @ThuisScoreRegulier, @ThuisScoreNV, @ThuisScoreS, @UitScore, @UitScoreRegulier,
                @UitScoreNV, @UitScoreS, @Klasse, @WedstrijdType, @CompetitieType, @Categorie, @MatchDateTime,
                @MatchDate, @Aanvangstijd, @Duration, @SpelType, @Aanduiding, @PouleCode, @Poule, @ThuisTeamID,
                @ThuisTeam, @UitTeamID, @UitTeam, @Opmerkingen, @VerenigingScheidsrechterCode, @VerenigingScheidsrechter,
                @OverigeOfficialCode, @OverigeOfficial, @Scheidsrechters, @KleedkamerThuis, @KleedkamerUit, @KleedkamerOfficial,
                @AccommodatieNaam, @AccommodatieStraat, @AccommodatiePlaats, @AccommodatieTelefoon, @AccommodatieRouteplanner,
                @ThuisTeamNaam, @ThuisTeamCode, @ThuisTeamWebsite, @ThuisTeamShirtKleur, @ThuisTeamStraat,
                @ThuisTeamPostcodePlaats, @ThuisTeamTelefoon, @ThuisTeamEmail, @UitTeamNaam, @UitTeamCode,
                @UitTeamWebsite, @UitTeamShirtKleur, @UitTeamStraat, @UitTeamPostcodePlaats, @UitTeamTelefoon, @UitTeamEmail
            )", conn);

        var wi = matchDetails.Wedstrijdinformatie;
        cmd.Parameters.AddWithValue("@WedstrijdCode",               wi.Wedstrijdnummer);
        cmd.Parameters.AddWithValue("@InternCode",                  wi.Wedstijdnummerintern);
        cmd.Parameters.AddWithValue("@VeldNaam",                    wi.Veldnaam             ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@VeldLocatie",                 wi.Veldlocatie          ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@VertrekTijd",                 wi.Vertrektijd          ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Rijder",                      wi.Rijder               ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisScore",                  wi.Thuisscore           ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisScoreRegulier",          wi.ThuisscoreRegulier   ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisScoreNV",                wi.ThuisscoreNv         ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisScoreS",                 wi.ThuisscoreS          ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitScore",                    wi.Uitscore             ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitScoreRegulier",            wi.UitscoreRegulier     ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitScoreNV",                  wi.UitscoreNv           ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitScoreS",                   wi.UitscoreS            ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Klasse",                      wi.Klasse               ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@WedstrijdType",               wi.Wedstrijdtype        ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@CompetitieType",              wi.Competitietype       ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Categorie",                   wi.Categorie            ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@MatchDateTime",               wi.Wedstrijddatetime    ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@MatchDate",                   wi.Wedstrijddatum       ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Aanvangstijd",
            TimeSpan.TryParse(wi.Aanvangstijd, out var ts) ? (object)ts : DBNull.Value);
        cmd.Parameters.AddWithValue("@Duration",                    wi.Duur                 ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@SpelType",                    wi.Speltype             ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Aanduiding",                  wi.Aanduiding           ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@PouleCode",
            int.TryParse(wi.Poulecode, out var pc) ? (object)pc : DBNull.Value);
        cmd.Parameters.AddWithValue("@Poule",                       wi.Poule                ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisTeamID",                 wi.Thuisteamid);
        cmd.Parameters.AddWithValue("@ThuisTeam",                   wi.Thuisteam            ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitTeamID",                   wi.Uitteamid);
        cmd.Parameters.AddWithValue("@UitTeam",                     wi.Uitteam              ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Opmerkingen",                 wi.Opmerkingen          ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@VerenigingScheidsrechterCode", matchDetails.Officials.Verenigingsscheidsrechtercode ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@VerenigingScheidsrechter",     matchDetails.Officials.Verenigingsscheidsrechter     ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@OverigeOfficialCode",          matchDetails.Officials.Overigeofficialcode           ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@OverigeOfficial",              matchDetails.Officials.Overigeofficial               ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Scheidsrechters",              matchDetails.Matchofficials.Scheidsrechters           ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@KleedkamerThuis",              matchDetails.Kleedkamers.Thuis                        ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@KleedkamerUit",                matchDetails.Kleedkamers.Uit                          ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@KleedkamerOfficial",           matchDetails.Kleedkamers.Official                     ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@AccommodatieNaam",             matchDetails.Accommodatie.Naam                        ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@AccommodatieStraat",           matchDetails.Accommodatie.Straat                      ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@AccommodatiePlaats",           matchDetails.Accommodatie.Plaats                      ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@AccommodatieTelefoon",         matchDetails.Accommodatie.Telefoon                    ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@AccommodatieRouteplanner",     matchDetails.Accommodatie.Routeplanner                ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisTeamNaam",                matchDetails.Thuisteam.Naam                           ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisTeamCode",                matchDetails.Thuisteam.Code                           ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisTeamWebsite",             matchDetails.Thuisteam.Website                        ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisTeamShirtKleur",          matchDetails.Thuisteam.Shirtkleur                     ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisTeamStraat",              matchDetails.Thuisteam.Straat                         ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisTeamPostcodePlaats",      matchDetails.Thuisteam.Postcodeplaats                 ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisTeamTelefoon",            matchDetails.Thuisteam.Telefoon                       ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThuisTeamEmail",               matchDetails.Thuisteam.Email                          ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitTeamNaam",                  matchDetails.Uitteam.Naam                             ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitTeamCode",                  matchDetails.Uitteam.Code                             ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitTeamWebsite",               matchDetails.Uitteam.Website                          ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitTeamShirtKleur",            matchDetails.Uitteam.Shirtkleur                       ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitTeamStraat",                matchDetails.Uitteam.Straat                           ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitTeamPostcodePlaats",        matchDetails.Uitteam.Postcodeplaats                   ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitTeamTelefoon",              matchDetails.Uitteam.Telefoon                         ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UitTeamEmail",                 matchDetails.Uitteam.Email                            ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        log.LogInformation("MATCHDETAILS - stg.matchdetails rij opgeslagen.");
    }

    // Gedeelde basisvelden voor programma én uitslagen.
    private static void AddMatchParams(SqlCommand cmd, Match match)
    {
        cmd.Parameters.AddWithValue("@wedstrijddatum",           match.wedstrijddatum            ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@wedstrijdcode",            match.wedstrijdcode);
        cmd.Parameters.AddWithValue("@wedstrijdnummer",          match.wedstrijdnummer);
        cmd.Parameters.AddWithValue("@datum",                    match.datum                     ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@wedstrijd",                match.wedstrijd                 ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@accommodatie",             match.accommodatie              ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@aanvangstijd",             match.aanvangstijd              ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@thuisteam",                match.thuisteam                 ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@thuisteamid",              match.thuisteamid               ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@thuisteamlogo",            match.thuisteamlogo             ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@thuisteamclubrelatiecode", match.thuisteamclubrelatiecode  ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@uitteamclubrelatiecode",   match.uitteamclubrelatiecode    ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@uitteam",                  match.uitteam                   ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@uitteamid",                match.uitteamid                 ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@uitteamlogo",              match.uitteamlogo               ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@competitiesoort",          match.competitiesoort           ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@status",                   match.status                    ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@meer",                     match.meer                      ?? (object)DBNull.Value);
    }
}
