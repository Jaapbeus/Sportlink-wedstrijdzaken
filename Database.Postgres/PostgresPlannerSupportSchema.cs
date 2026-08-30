namespace Database.Postgres;

/// <summary>
/// Minimale Postgres-DDL voor de operationele configuratietabellen waar
/// <see cref="PostgresPlannerViewGenerator"/> van afhangt (#819): <c>public.appsettings</c>,
/// <c>public.velden</c>, <c>public.speeltijden</c> en <c>planner.geplandewedstrijden</c>.
/// <para>
/// <b>Scope-afbakening.</b> Deze DDL is bewust beperkt tot precies de kolommen die de
/// planner-kernview nodig heeft — geen volledige 1-op-1-vertaling van elke kolom uit
/// <c>Database/dbo/Tables/*.sql</c>. De volledige, declaratieve eindschema-migratie voor de
/// Postgres-tier (élke configuratietabel, niet alleen deze vier) is de scope van #821
/// ("Nieuwe migratie-aanpak Database.Postgres/"). Deze klasse mag dus vervangen worden zodra
/// #821 een generieke migratieroute oplevert — tot die tijd is dit de kleinste schema-subset
/// waarmee #819 zelfstandig empirisch te verifiëren is.
/// </para>
/// <para>
/// Schemanamen volgen de casing-conventie uit <c>docs/ARCHITECTUUR-DATABASE-TIERS.md</c> §3:
/// SQL Server's generieke <c>dbo</c> wordt Postgres' idiomatische <c>public</c> (nooit <c>dbo</c>
/// letterlijk overgenomen); <c>planner</c> is een betekenisvolle domeinschemanaam en blijft gelijk.
/// Alle identifiers zijn lowercase en ongequote, dus Postgres' automatische lowercase-folding kan
/// nooit een mismatch veroorzaken (zie hetzelfde document, "Postgres-specifieke valkuil").
/// </para>
/// </summary>
public static class PostgresPlannerSupportSchema
{
    public const string CreateSchemas = """
        CREATE SCHEMA IF NOT EXISTS planner;
        """;

    public const string CreateAppSettings = """
        CREATE TABLE IF NOT EXISTS public.appsettings (
            clubcode VARCHAR(20) NOT NULL,
            accommodatie TEXT NULL,
            syncenabled BOOLEAN NOT NULL DEFAULT TRUE
        );
        """;

    public const string CreateVelden = """
        CREATE TABLE IF NOT EXISTS public.velden (
            veldnummer INTEGER NOT NULL PRIMARY KEY,
            veldnaam VARCHAR(50) NOT NULL,
            actief BOOLEAN NOT NULL DEFAULT TRUE,
            clubcode VARCHAR(20) NOT NULL
        );
        """;

    public const string CreateSpeeltijden = """
        CREATE TABLE IF NOT EXISTS public.speeltijden (
            leeftijd VARCHAR(10) NOT NULL,
            veldafmeting DECIMAL(4, 2) NOT NULL,
            wedstrijdtotaal INTEGER NOT NULL,
            clubcode VARCHAR(20) NOT NULL,
            PRIMARY KEY (leeftijd, clubcode)
        );
        """;

    public const string CreateGeplandeWedstrijden = """
        CREATE TABLE IF NOT EXISTS planner.geplandewedstrijden (
            id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            datum DATE NOT NULL,
            aanvangstijd TIME NOT NULL,
            eindtijd TIME NOT NULL,
            veldnummer INTEGER NOT NULL,
            velddeelgebruik DECIMAL(4, 2) NOT NULL DEFAULT 1.00,
            leeftijdscategorie VARCHAR(10) NULL,
            teamnaam VARCHAR(100) NULL,
            tegenstander VARCHAR(100) NULL,
            status VARCHAR(20) NOT NULL DEFAULT 'Te bevestigen',
            isvervallen BOOLEAN NOT NULL DEFAULT FALSE,
            sportlinkwedstrijdcode BIGINT NULL,
            clubcode VARCHAR(20) NOT NULL
        );
        """;

    /// <summary>Alle DDL-statements in de juiste volgorde — handig voor testopbouw.</summary>
    public static IReadOnlyList<string> AllInOrder =>
    [
        CreateSchemas,
        CreateAppSettings,
        CreateVelden,
        CreateSpeeltijden,
        CreateGeplandeWedstrijden,
    ];
}
