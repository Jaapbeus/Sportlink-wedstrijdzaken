namespace Database.Postgres;

/// <summary>
/// De drie vandaag bestaande ETL-entiteiten, als <see cref="EntityDefinition"/> — de
/// Postgres-tier-tegenhanger van <c>mta.source_target_mapping</c> (#818, concrete stap 1).
/// <para>
/// Kolomlijst en -typen zijn overgenomen uit de daadwerkelijke SQL Server-schemabestanden
/// (<c>Database/his/Tables/Teams.sql</c>, <c>Matches.sql</c>, <c>MatchDetails.sql</c> en hun
/// <c>stg</c>-tegenhangers), niet aangenomen. Twee correcties op de "indicatieve" aannames uit de
/// oorspronkelijke issuetekst, gevonden tijdens deze inventarisatie:
/// </para>
/// <list type="bullet">
/// <item>Geen van de drie his-tabellen heeft een los IDENTITY-surrogate-sleutelkolom — de
/// synthetische <c>bk_</c>-kolom (via een unieke index, geen PK-constraint) is vandaag al de
/// enige sleutel. <see cref="PostgresSchemaGenerator"/> voegt daarom ook geen Id-kolom toe.</item>
/// <item><c>mta_deleted</c> is in alle drie tabellen een nullable <c>DATETIME</c>
/// (deletie-tijdstip), geen boolean-vlag.</item>
/// </list>
/// </summary>
public static class KnownEntities
{
    public static EntityDefinition Teams => EntityDefinition.Create(
        entityName: "teams",
        columns:
        [
            new ColumnDefinition("teamcode", ProviderAgnosticType.BigInt),
            new ColumnDefinition("lokaleteamcode", ProviderAgnosticType.BigInt),
            new ColumnDefinition("poulecode", ProviderAgnosticType.BigInt),
            new ColumnDefinition("teamnaam", ProviderAgnosticType.VarChar, Length: 100),
            new ColumnDefinition("competitienaam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("klasse", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("poule", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("klassepoule", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("spelsoort", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("competitiesoort", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("geslacht", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("teamsoort", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("leeftijdscategorie", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("kalespelsoort", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("speeldag", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("speeldagteam", ProviderAgnosticType.VarChar, Length: 100),
            new ColumnDefinition("more", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("ClubCode", ProviderAgnosticType.VarChar, Length: 20),
        ],
        businessKey: ["teamcode", "lokaleteamcode", "poulecode"],
        hasClubCode: true);

    public static EntityDefinition Matches => EntityDefinition.Create(
        entityName: "matches",
        columns:
        [
            new ColumnDefinition("wedstrijddatum", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("wedstrijdcode", ProviderAgnosticType.BigInt),
            new ColumnDefinition("wedstrijdnummer", ProviderAgnosticType.BigInt),
            new ColumnDefinition("datum", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("wedstrijd", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("accommodatie", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("aanvangstijd", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("thuisteam", ProviderAgnosticType.VarChar, Length: 100),
            new ColumnDefinition("thuisteamid", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("thuisteamlogo", ProviderAgnosticType.VarChar, Length: 1000),
            new ColumnDefinition("thuisteamclubrelatiecode", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("uitteamclubrelatiecode", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("uitteam", ProviderAgnosticType.VarChar, Length: 100),
            new ColumnDefinition("uitteamid", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("uitteamlogo", ProviderAgnosticType.VarChar, Length: 1000),
            new ColumnDefinition("competitiesoort", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("status", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("meer", ProviderAgnosticType.VarChar, Length: 1000),
            new ColumnDefinition("teamnaam", ProviderAgnosticType.VarChar, Length: 100),
            new ColumnDefinition("teamvolgorde", ProviderAgnosticType.Integer),
            new ColumnDefinition("competitie", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("klasse", ProviderAgnosticType.VarChar, Length: 100),
            new ColumnDefinition("poule", ProviderAgnosticType.VarChar, Length: 100),
            new ColumnDefinition("klassepoule", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("kaledatum", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("vertrektijd", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("verzameltijd", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("scheidsrechters", ProviderAgnosticType.VarChar, Length: 500),
            new ColumnDefinition("scheidsrechter", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("veld", ProviderAgnosticType.VarChar, Length: 100),
            new ColumnDefinition("veld_subpositie", ProviderAgnosticType.VarChar, Length: 5),
            new ColumnDefinition("locatie", ProviderAgnosticType.VarChar, Length: 100),
            new ColumnDefinition("plaats", ProviderAgnosticType.VarChar, Length: 100),
            new ColumnDefinition("rijders", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("kleedkamerthuisteam", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("kleedkameruitteam", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("kleedkamerscheidsrechter", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("datumopgemaakt", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("uitslag", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("uitslag-regulier", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("uitslag-nv", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("uitslag-s", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("competitienaam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("eigenteam", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("sportomschrijving", ProviderAgnosticType.VarChar, Length: 100),
            new ColumnDefinition("verenigingswedstrijd", ProviderAgnosticType.VarChar, Length: 50),
            new ColumnDefinition("ClubCode", ProviderAgnosticType.VarChar, Length: 20),
        ],
        businessKey: ["wedstrijdcode"],
        hasClubCode: true);

    public static EntityDefinition MatchDetails => EntityDefinition.Create(
        entityName: "matchdetails",
        columns:
        [
            new ColumnDefinition("WedstrijdCode", ProviderAgnosticType.Integer, IsNullable: false),
            new ColumnDefinition("InternCode", ProviderAgnosticType.Integer),
            new ColumnDefinition("VeldNaam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("VeldLocatie", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("VertrekTijd", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("Rijder", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("ThuisScore", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("ThuisScoreRegulier", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("ThuisScoreNV", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("ThuisScoreS", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("UitScore", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("UitScoreRegulier", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("UitScoreNV", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("UitScoreS", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("Klasse", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("WedstrijdType", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("CompetitieType", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("Categorie", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("MatchDateTime", ProviderAgnosticType.Timestamp),
            new ColumnDefinition("MatchDate", ProviderAgnosticType.Date),
            new ColumnDefinition("Aanvangstijd", ProviderAgnosticType.Time),
            new ColumnDefinition("Duration", ProviderAgnosticType.Integer),
            new ColumnDefinition("SpelType", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("Aanduiding", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("PouleCode", ProviderAgnosticType.Integer),
            new ColumnDefinition("Poule", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("ThuisTeamID", ProviderAgnosticType.Integer),
            new ColumnDefinition("ThuisTeam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("UitTeamID", ProviderAgnosticType.Integer),
            new ColumnDefinition("UitTeam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("Opmerkingen", ProviderAgnosticType.Text),
            new ColumnDefinition("VerenigingScheidsrechterCode", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("VerenigingScheidsrechter", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("OverigeOfficialCode", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("OverigeOfficial", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("Scheidsrechters", ProviderAgnosticType.VarChar, Length: 1000),
            new ColumnDefinition("KleedkamerThuis", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("KleedkamerUit", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("KleedkamerOfficial", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("AccommodatieNaam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("AccommodatieStraat", ProviderAgnosticType.VarChar, Length: 150),
            new ColumnDefinition("AccommodatiePlaats", ProviderAgnosticType.VarChar, Length: 150),
            new ColumnDefinition("AccommodatieTelefoon", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("AccommodatieRouteplanner", ProviderAgnosticType.VarChar, Length: 1000),
            new ColumnDefinition("ThuisTeamNaam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("ThuisTeamCode", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("ThuisTeamWebsite", ProviderAgnosticType.VarChar, Length: 1000),
            new ColumnDefinition("ThuisTeamShirtKleur", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("ThuisTeamStraat", ProviderAgnosticType.VarChar, Length: 150),
            new ColumnDefinition("ThuisTeamPostcodePlaats", ProviderAgnosticType.VarChar, Length: 150),
            new ColumnDefinition("ThuisTeamTelefoon", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("ThuisTeamEmail", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("UitTeamNaam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("UitTeamCode", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("UitTeamWebsite", ProviderAgnosticType.VarChar, Length: 1000),
            new ColumnDefinition("UitTeamShirtKleur", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("UitTeamStraat", ProviderAgnosticType.VarChar, Length: 150),
            new ColumnDefinition("UitTeamPostcodePlaats", ProviderAgnosticType.VarChar, Length: 150),
            new ColumnDefinition("UitTeamTelefoon", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("UitTeamEmail", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("ClubCode", ProviderAgnosticType.VarChar, Length: 20),
        ],
        businessKey: ["WedstrijdCode"],
        hasClubCode: true);

    /// <summary>Alle drie entiteiten — handig voor "genereer voor elke bekende entiteit"-loops.</summary>
    public static IReadOnlyList<EntityDefinition> All => [Teams, Matches, MatchDetails];
}
