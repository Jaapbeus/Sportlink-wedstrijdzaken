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

    internal static async Task<List<string>> GetWedstrijdcodesAsync()
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

    internal static async Task SaveTeamsAsync(List<Team> teams, string clubCode, ILogger log)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        foreach (var team in teams)
        {
            using var cmd = new SqlCommand(@"
                INSERT INTO [stg].[teams]
                    ([teamcode],[lokaleteamcode],[poulecode],[teamnaam],[competitienaam],[klasse],
                     [poule],[klassepoule],[spelsoort],[competitiesoort],[geslacht],[teamsoort],
                     [leeftijdscategorie],[kalespelsoort],[speeldag],[speeldagteam],[more],[ClubCode])
                VALUES
                    (@teamcode,@lokaleteamcode,@poulecode,@teamnaam,@competitienaam,@klasse,
                     @poule,@klassepoule,@spelsoort,@competitiesoort,@geslacht,@teamsoort,
                     @leeftijdscategorie,@kalespelsoort,@speeldag,@speeldagteam,@more,@ClubCode)", conn);
            cmd.Parameters.AddWithValue("@ClubCode",          clubCode ?? (object)DBNull.Value);
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

    internal static async Task<int> SaveProgrammaAsync(List<Match> matches, string clubCode, ILogger log)
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
                    ,[ClubCode]
                ) VALUES (
                     @wedstrijddatum,@wedstrijdcode,@wedstrijdnummer,@datum,@wedstrijd
                    ,@accommodatie,@aanvangstijd,@thuisteam,@thuisteamid,@thuisteamlogo
                    ,@thuisteamclubrelatiecode,@uitteamclubrelatiecode,@uitteam,@uitteamid
                    ,@uitteamlogo,@competitiesoort,@status,@meer
                    ,@teamnaam,@teamvolgorde,@competitie,@klasse,@poule,@klassepoule
                    ,@kaledatum,@vertrektijd,@verzameltijd,@scheidsrechters,@scheidsrechter
                    ,@veld,@locatie,@plaats,@rijders
                    ,@kleedkamerthuisteam,@kleedkameruitteam,@kleedkamerscheidsrechter
                    ,@ClubCode
                )", conn);
            AddMatchParams(cmd, match);
            cmd.Parameters.AddWithValue("@ClubCode",                   clubCode                         ?? (object)DBNull.Value);
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

    /// <summary>
    /// Kolommen die alleen <c>/programma</c> levert en <c>/uitslagen</c> niet (#1547).
    /// </summary>
    private static readonly string[] AlleenProgrammaKolommen =
    [
        "teamnaam", "teamvolgorde", "competitie", "klasse", "poule", "klassepoule", "kaledatum",
        "vertrektijd", "verzameltijd", "scheidsrechters", "scheidsrechter", "veld", "veld_subpositie",
        "locatie", "plaats", "rijders", "kleedkamerthuisteam", "kleedkameruitteam", "kleedkamerscheidsrechter",
    ];

    /// <summary>
    /// Vult stg-rijen die <see cref="MergeUitslagenAsync"/> invoegde aan met de programma-gegevens die
    /// al in <c>his.matches</c> staan (#1547). Een gespeelde wedstrijd staat niet meer in
    /// <c>/programma</c>, dus komt hij alleen via <c>/uitslagen</c> in stg — zonder kaledatum, veld,
    /// teamnaam en kleedkamers — en de merge overschreef daarmee de complete his-rij met NULL's.
    /// Zonder his-rij wordt <c>kaledatum</c> uit <c>wedstrijddatum</c> afgeleid.
    /// Kolommen die niet in stg of his bestaan (bv. veld_subpositie op een oudere installatie) worden overgeslagen.
    /// </summary>
    internal static async Task<int> VulUitslagRijenAanUitHisAsync(string clubCode, ILogger log)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        var aangevuld = 0;

        if (await ScalarBoolAsync(conn, "SELECT CASE WHEN OBJECT_ID('his.matches') IS NOT NULL THEN 1 ELSE 0 END"))
        {
            var sets = new List<string>();
            foreach (var k in AlleenProgrammaKolommen)
            {
                if (await ScalarBoolAsync(conn,
                        $"SELECT CASE WHEN COL_LENGTH('stg.matches', '{k}') IS NOT NULL AND COL_LENGTH('his.matches', '{k}') IS NOT NULL THEN 1 ELSE 0 END"))
                    // Review #1547 R1-F3: de uitslagdatum is actueler dan de kaledatum in his.
                    sets.Add(k == "kaledatum"
                        ? $"s.[kaledatum] = COALESCE({KaledatumUitWedstrijddatum("s.[wedstrijddatum]")}, h.[kaledatum])"
                        : $"s.[{k}] = COALESCE(s.[{k}], h.[{k}])");
            }
            if (sets.Count > 0)
            {
                using var vulAan = new SqlCommand($@"
                    UPDATE s SET {string.Join(", ", sets)}
                    FROM [stg].[matches] s
                    INNER JOIN [his].[matches] h ON h.[wedstrijdcode] = s.[wedstrijdcode] AND h.[ClubCode] = s.[ClubCode]
                    WHERE s.[ClubCode] = @clubcode AND s.[kaledatum] IS NULL", conn);
                vulAan.Parameters.AddWithValue("@clubcode", clubCode);
                aangevuld = await vulAan.ExecuteNonQueryAsync();
            }
        }

        using var datum = new SqlCommand($@"
            UPDATE [stg].[matches] SET [kaledatum] = {KaledatumUitWedstrijddatum("[wedstrijddatum]")}
            WHERE [ClubCode] = @clubcode AND [kaledatum] IS NULL", conn);
        datum.Parameters.AddWithValue("@clubcode", clubCode);
        await datum.ExecuteNonQueryAsync();
        log.LogInformation("MATCHES/UITSLAGEN - {Aangevuld} uitslag-rijen aangevuld uit his.matches.", aangevuld);
        return aangevuld;
    }

    /// <summary>Lokale datum van Sportlinks wedstrijddatum in kaledatum-vorm, of NULL als het geen datum is.</summary>
    private static string KaledatumUitWedstrijddatum(string kolom)
        => $"CASE WHEN {kolom} LIKE '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]%' THEN LEFT({kolom}, 10) + ' 00:00:00.00' END";

    private static async Task<bool> ScalarBoolAsync(SqlConnection conn, string sql)
    {
        using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    internal static async Task<int> MergeUitslagenAsync(List<Match> matches, string clubCode, ILogger log)
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
                        ,[ClubCode]
                    ) VALUES (
                         @wedstrijddatum,@wedstrijdcode,@wedstrijdnummer,@datum,@wedstrijd
                        ,@accommodatie,@aanvangstijd,@thuisteam,@thuisteamid,@thuisteamlogo
                        ,@thuisteamclubrelatiecode,@uitteamclubrelatiecode,@uitteam,@uitteamid
                        ,@uitteamlogo,@competitiesoort,@status,@meer
                        ,@datumopgemaakt,@uitslag,@uitslagregulier,@uitslagnv,@uitslags
                        ,@competitienaam,@eigenteam,@sportomschrijving,@verenigingswedstrijd
                        ,@ClubCode
                    )", conn);
            AddMatchParams(cmd, match);
            cmd.Parameters.AddWithValue("@ClubCode",            clubCode                 ?? (object)DBNull.Value);
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

    internal static async Task SaveMatchDetailsAsync(MatchDetails matchDetails, string clubCode, ILogger log)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            -- #1547: ontdubbelen op InternCode (= his.matches.wedstrijdcode, uniek). WedstrijdCode is het
            -- wedstrijdnummer en bij clubwedstrijden vaak 1.
            IF NOT EXISTS (SELECT 1 FROM [stg].[matchdetails] WHERE InternCode = @InternCode)
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
                UitTeamWebsite, UitTeamShirtKleur, UitTeamStraat, UitTeamPostcodePlaats, UitTeamTelefoon, UitTeamEmail,
                ClubCode
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
                @UitTeamWebsite, @UitTeamShirtKleur, @UitTeamStraat, @UitTeamPostcodePlaats, @UitTeamTelefoon, @UitTeamEmail,
                @ClubCode
            )", conn);
        BindMatchDetailsParameters(cmd, matchDetails, clubCode);
        await cmd.ExecuteNonQueryAsync();
        log.LogInformation("MATCHDETAILS - stg.matchdetails rij opgeslagen.");
    }

    internal static void BindMatchDetailsParameters(SqlCommand cmd, MatchDetails matchDetails, string clubCode)
    {
        Voeg(cmd, "@ClubCode", clubCode);
        AddWedstrijdinformatieParams(cmd, matchDetails.Wedstrijdinformatie);
        AddOfficialsParams(cmd, matchDetails.Officials, matchDetails.Matchofficials, matchDetails.Kleedkamers);
        AddAccommodatieEnTeamParams(cmd, matchDetails.Accommodatie, matchDetails.Thuisteam, matchDetails.Uitteam);
    }

    // Null wordt DBNull; de hulpmethode houdt de ??-vertakkingen uit de binders (CA1502, #1624).
    private static void Voeg(SqlCommand cmd, string naam, object? waarde) =>
        cmd.Parameters.AddWithValue(naam, waarde ?? DBNull.Value);

    private static void AddWedstrijdinformatieParams(SqlCommand cmd, Wedstrijdinformatie wi)
    {
        Voeg(cmd, "@WedstrijdCode", wi.Wedstrijdnummer);
        Voeg(cmd, "@InternCode", wi.Wedstijdnummerintern);
        Voeg(cmd, "@VeldNaam", wi.Veldnaam);
        Voeg(cmd, "@VeldLocatie", wi.Veldlocatie);
        Voeg(cmd, "@VertrekTijd", wi.Vertrektijd);
        Voeg(cmd, "@Rijder", wi.Rijder);
        Voeg(cmd, "@ThuisScore", wi.Thuisscore);
        Voeg(cmd, "@ThuisScoreRegulier", wi.ThuisscoreRegulier);
        Voeg(cmd, "@ThuisScoreNV", wi.ThuisscoreNv);
        Voeg(cmd, "@ThuisScoreS", wi.ThuisscoreS);
        Voeg(cmd, "@UitScore", wi.Uitscore);
        Voeg(cmd, "@UitScoreRegulier", wi.UitscoreRegulier);
        Voeg(cmd, "@UitScoreNV", wi.UitscoreNv);
        Voeg(cmd, "@UitScoreS", wi.UitscoreS);
        Voeg(cmd, "@Klasse", wi.Klasse);
        Voeg(cmd, "@WedstrijdType", wi.Wedstrijdtype);
        Voeg(cmd, "@CompetitieType", wi.Competitietype);
        Voeg(cmd, "@Categorie", wi.Categorie);
        Voeg(cmd, "@MatchDateTime", wi.Wedstrijddatetime);
        Voeg(cmd, "@MatchDate", wi.Wedstrijddatum);
        Voeg(cmd, "@Aanvangstijd", TimeSpan.TryParse(wi.Aanvangstijd, out var ts) ? ts : null);
        Voeg(cmd, "@Duration", wi.Duur);
        Voeg(cmd, "@SpelType", wi.Speltype);
        Voeg(cmd, "@Aanduiding", wi.Aanduiding);
        Voeg(cmd, "@PouleCode", int.TryParse(wi.Poulecode, out var pc) ? pc : null);
        Voeg(cmd, "@Poule", wi.Poule);
        Voeg(cmd, "@ThuisTeamID", wi.Thuisteamid);
        Voeg(cmd, "@ThuisTeam", wi.Thuisteam);
        Voeg(cmd, "@UitTeamID", wi.Uitteamid);
        Voeg(cmd, "@UitTeam", wi.Uitteam);
        Voeg(cmd, "@Opmerkingen", wi.Opmerkingen);
    }

    private static void AddOfficialsParams(SqlCommand cmd, Officials officials, Matchofficials matchofficials, Kleedkamers kleedkamers)
    {
        Voeg(cmd, "@VerenigingScheidsrechterCode", officials.Verenigingsscheidsrechtercode);
        Voeg(cmd, "@VerenigingScheidsrechter", officials.Verenigingsscheidsrechter);
        Voeg(cmd, "@OverigeOfficialCode", officials.Overigeofficialcode);
        Voeg(cmd, "@OverigeOfficial", officials.Overigeofficial);
        Voeg(cmd, "@Scheidsrechters", matchofficials.Scheidsrechters);
        Voeg(cmd, "@KleedkamerThuis", kleedkamers.Thuis);
        Voeg(cmd, "@KleedkamerUit", kleedkamers.Uit);
        Voeg(cmd, "@KleedkamerOfficial", kleedkamers.Official);
    }

    private static void AddAccommodatieEnTeamParams(SqlCommand cmd, Accommodatie accommodatie, Thuisteam thuisteam, Uitteam uitteam)
    {
        Voeg(cmd, "@AccommodatieNaam", accommodatie.Naam);
        Voeg(cmd, "@AccommodatieStraat", accommodatie.Straat);
        Voeg(cmd, "@AccommodatiePlaats", accommodatie.Plaats);
        Voeg(cmd, "@AccommodatieTelefoon", accommodatie.Telefoon);
        Voeg(cmd, "@AccommodatieRouteplanner", accommodatie.Routeplanner);
        Voeg(cmd, "@ThuisTeamNaam", thuisteam.Naam);
        Voeg(cmd, "@ThuisTeamCode", thuisteam.Code);
        Voeg(cmd, "@ThuisTeamWebsite", thuisteam.Website);
        Voeg(cmd, "@ThuisTeamShirtKleur", thuisteam.Shirtkleur);
        Voeg(cmd, "@ThuisTeamStraat", thuisteam.Straat);
        Voeg(cmd, "@ThuisTeamPostcodePlaats", thuisteam.Postcodeplaats);
        Voeg(cmd, "@ThuisTeamTelefoon", thuisteam.Telefoon);
        Voeg(cmd, "@ThuisTeamEmail", thuisteam.Email);
        Voeg(cmd, "@UitTeamNaam", uitteam.Naam);
        Voeg(cmd, "@UitTeamCode", uitteam.Code);
        Voeg(cmd, "@UitTeamWebsite", uitteam.Website);
        Voeg(cmd, "@UitTeamShirtKleur", uitteam.Shirtkleur);
        Voeg(cmd, "@UitTeamStraat", uitteam.Straat);
        Voeg(cmd, "@UitTeamPostcodePlaats", uitteam.Postcodeplaats);
        Voeg(cmd, "@UitTeamTelefoon", uitteam.Telefoon);
        Voeg(cmd, "@UitTeamEmail", uitteam.Email);
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
