using Database.Postgres;
using Npgsql;

namespace FunctionApp.Postgres;

/// <summary>
/// Configuratielaag voor de Postgres-tier (#891) — het Postgres-equivalent van
/// <c>SystemUtilities.DatabaseConfig</c> in de SQL Server-tier.
/// <para>
/// Connectiestring komt uit <c>POSTGRES_CONNECTION_STRING</c> — dezelfde omgevingsvariabele-naam
/// die <c>Database.Postgres.Cli</c> (#821) al gebruikt voor het migratiepad. Nooit een tweede
/// naamschema voor dezelfde soort waarde.
/// </para>
/// <para>
/// <b>Bewust geen <c>Pooling=false</c>:</b> de SQL Server-tier zet dat om een specifieke reden
/// (#808 — een pooled connectie blokkeert Azure SQL's serverless auto-pause). Dat is een
/// eigenschap van díe specifieke hostingkeuze, niet een algemene regel — zonder bevestiging dat de
/// Postgres-tier op een vergelijkbare auto-pausende laag draait, is de standaard Npgsql-pooling
/// (die verbindingen juist efficiënter hergebruikt) de juiste default.
/// </para>
/// </summary>
public static class PostgresDatabaseConfig
{
    private const string ConnectionStringEnvVar = "POSTGRES_CONNECTION_STRING";

    // Eén static initializer voor alles wat uit de omgevingsvariabele volgt. Gooit die (variabele
    // ontbreekt, ongeldige syntax, expliciet onversleuteld naar een niet-lokale host), dan gooit
    // élke toegang hieronder een TypeInitializationException — /api/health vangt dat als
    // "unconfigured" (503). Zie het #976- en #1095-incident hieronder.
    private static readonly PostgresConnectionNormalization Normalization =
        BuildNormalization(Environment.GetEnvironmentVariable(ConnectionStringEnvVar));

    public static string ConnectionString => Normalization.ConnectionString;

    /// <summary>De TLS-modus die daadwerkelijk op elke verbinding geldt (bijv. <c>VerifyFull</c>, <c>Require</c>).</summary>
    public static string EffectiveSslMode => Normalization.EffectiveSslMode.ToString();

    /// <summary>
    /// #1095: <c>null</c> als het TLS-beleid volledig gehaald is; anders een waarschuwing zonder
    /// host of credentials, bedoeld voor <c>/api/health</c> en het functielog. De applicatie draait
    /// dan wél (versleuteld, maar zonder volledige certificaatvalidatie) — een niet-werkende
    /// productie is geen beveiligingswinst.
    /// </summary>
    public static string? TlsWarning => Normalization.TlsWarning;

    // internal + parameter (#859): de static initializer hierboven is al gevuld vóórdat een test
    // kan draaien — dit maakt het "geen bruikbare connectiereeks"-pad los daarvan testbaar, zelfde
    // precedent als SystemUtilities.DatabaseConfig.BuildConnectionString.
    internal static string BuildConnectionString(string? raw) => BuildNormalization(raw).ConnectionString;

    // #976-incident: de eerste echte productiecutover zette exact de URI-vorm die Supabase's
    // dashboard toont (postgresql://gebruiker:wachtwoord@host:5432/database) als Azure-instelling —
    // dezelfde vorm die MigrationTools/SqlServerToPostgresCopy al accepteert via
    // PostgresConnectionStringNormalizer, maar déze klasse riep NpgsqlConnectionStringBuilder nog
    // rechtstreeks aan op de ruwe waarde. Omdat de initializer eager is (bij het laden van de
    // klasse), gooide dat een parseerfout bij de allereerste opstart van de Function App zelf —
    // health-endpoint gaf onmiddellijk en aanhoudend 503, geen cold-start-vertraging maar een
    // echte crash. Genormaliseerd vóór gebruik lost dit op.
    //
    // #1095-incident (release v3.3.0.0): #1004 liet diezelfde normalizer een exceptie gooien voor
    // elke niet-lokale host zonder sslmode=verify-full. Zelfde mechanisme, zelfde symptoom — de
    // productie-instelling had geen ?sslmode= en de hele database-tier viel uit. De normalizer
    // meldt dat nu als TlsWarning in plaats van te gooien.
    internal static PostgresConnectionNormalization BuildNormalization(string? raw)
    {
        if (raw is null)
            throw new InvalidOperationException($"Omgevingsvariabele '{ConnectionStringEnvVar}' is niet gezet.");

        var normalization = PostgresConnectionStringNormalizer.NormalizeWithDiagnostics(raw);

        // Herkenbare toepassingsnaam op elke verbinding (#863-precedent) — een onafhankelijke
        // bevestiging (bijv. via pg_stat_activity) naast wat de applicatie in /api/health over
        // zichzelf zegt.
        var withApplicationName = new NpgsqlConnectionStringBuilder(normalization.ConnectionString)
        {
            ApplicationName = "SportlinkFunctionAppPostgres",
        }.ConnectionString;

        return normalization with { ConnectionString = withApplicationName };
    }
}
