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
/// <para>
/// <b>Kolomcasing (#855):</b> de SQL Server-bronbestanden zijn PascalCase (<c>ClubCode</c>,
/// <c>MatchDetails.sql</c> vrijwel volledig). Deze klasse neemt die casing bewust NIET over —
/// docs/ARCHITECTUUR-DATABASE-TIERS.md §3 schrijft voor de Postgres-boom expliciet lowercase
/// snake_case voor, en <see cref="PostgresIdentifier.Quote"/> quote't onvoorwaardelijk, dus een
/// PascalCase kolomnaam hier landt letterlijk zo in de database — elke latere, ongequote
/// verwijzing (<c>WHERE clubcode = …</c>) zou dan stuklopen. Een eerdere versie week hier
/// (per ongeluk) van af voor <c>ClubCode</c> en de volledige <c>matchdetails</c>-entiteit;
/// <see cref="EntityDefinition.Create"/> forceert deze conventie nu ook af (zie die klasse).
/// </para>
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
            new ColumnDefinition("clubcode", ProviderAgnosticType.VarChar, Length: 20),
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
            new ColumnDefinition("clubcode", ProviderAgnosticType.VarChar, Length: 20),
        ],
        businessKey: ["wedstrijdcode"],
        hasClubCode: true);

    public static EntityDefinition MatchDetails => EntityDefinition.Create(
        entityName: "matchdetails",
        columns:
        [
            new ColumnDefinition("wedstrijdcode", ProviderAgnosticType.Integer, IsNullable: false),
            new ColumnDefinition("interncode", ProviderAgnosticType.Integer),
            new ColumnDefinition("veldnaam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("veldlocatie", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("vertrektijd", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("rijder", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("thuisscore", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("thuisscoreregulier", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("thuisscorenv", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("thuisscores", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("uitscore", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("uitscoreregulier", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("uitscorenv", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("uitscores", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("klasse", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("wedstrijdtype", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("competitietype", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("categorie", ProviderAgnosticType.VarChar, Length: 20),
            new ColumnDefinition("matchdatetime", ProviderAgnosticType.Timestamp),
            new ColumnDefinition("matchdate", ProviderAgnosticType.Date),
            new ColumnDefinition("aanvangstijd", ProviderAgnosticType.Time),
            new ColumnDefinition("duration", ProviderAgnosticType.Integer),
            new ColumnDefinition("speltype", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("aanduiding", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("poulecode", ProviderAgnosticType.Integer),
            new ColumnDefinition("poule", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("thuisteamid", ProviderAgnosticType.Integer),
            new ColumnDefinition("thuisteam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("uitteamid", ProviderAgnosticType.Integer),
            new ColumnDefinition("uitteam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("opmerkingen", ProviderAgnosticType.Text),
            new ColumnDefinition("verenigingscheidsrechtercode", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("verenigingscheidsrechter", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("overigeofficialcode", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("overigeofficial", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("scheidsrechters", ProviderAgnosticType.VarChar, Length: 1000),
            new ColumnDefinition("kleedkamerthuis", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("kleedkameruit", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("kleedkamerofficial", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("accommodatienaam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("accommodatiestraat", ProviderAgnosticType.VarChar, Length: 150),
            new ColumnDefinition("accommodatieplaats", ProviderAgnosticType.VarChar, Length: 150),
            new ColumnDefinition("accommodatietelefoon", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("accommodatierouteplanner", ProviderAgnosticType.VarChar, Length: 1000),
            new ColumnDefinition("thuisteamnaam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("thuisteamcode", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("thuisteamwebsite", ProviderAgnosticType.VarChar, Length: 1000),
            new ColumnDefinition("thuisteamshirtkleur", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("thuisteamstraat", ProviderAgnosticType.VarChar, Length: 150),
            new ColumnDefinition("thuisteampostcodeplaats", ProviderAgnosticType.VarChar, Length: 150),
            new ColumnDefinition("thuisteamtelefoon", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("thuisteamemail", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("uitteamnaam", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("uitteamcode", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("uitteamwebsite", ProviderAgnosticType.VarChar, Length: 1000),
            new ColumnDefinition("uitteamshirtkleur", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("uitteamstraat", ProviderAgnosticType.VarChar, Length: 150),
            new ColumnDefinition("uitteampostcodeplaats", ProviderAgnosticType.VarChar, Length: 150),
            new ColumnDefinition("uitteamtelefoon", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("uitteamemail", ProviderAgnosticType.VarChar, Length: 200),
            new ColumnDefinition("clubcode", ProviderAgnosticType.VarChar, Length: 20),
        ],
        businessKey: ["wedstrijdcode"],
        hasClubCode: true);

    /// <summary>Alle drie entiteiten — handig voor "genereer voor elke bekende entiteit"-loops.</summary>
    public static IReadOnlyList<EntityDefinition> All => [Teams, Matches, MatchDetails];
}
