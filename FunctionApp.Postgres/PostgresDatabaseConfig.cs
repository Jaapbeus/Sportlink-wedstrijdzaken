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

    public static string ConnectionString { get; } = BuildConnectionString(Environment.GetEnvironmentVariable(ConnectionStringEnvVar));

    // internal + parameter (#859): ConnectionString hierboven is static readonly en dus al gevuld
    // vóórdat een test kan draaien — dit maakt het "geen bruikbare connectiereeks"-pad los daarvan
    // testbaar, zelfde precedent als SystemUtilities.DatabaseConfig.BuildConnectionString.
    //
    // #976-incident: de eerste echte productiecutover zette exact de URI-vorm die Supabase's
    // dashboard toont (postgresql://gebruiker:wachtwoord@host:5432/database) als Azure-instelling —
    // dezelfde vorm die MigrationTools/SqlServerToPostgresCopy al accepteert via
    // PostgresConnectionStringNormalizer, maar déze klasse riep NpgsqlConnectionStringBuilder nog
    // rechtstreeks aan op de ruwe waarde. Omdat ConnectionString hierboven static readonly is
    // (eager, bij het laden van de klasse), gooide dat een parseerfout bij de allereerste opstart
    // van de Function App zelf — health-endpoint gaf onmiddellijk en aanhoudend 503, geen
    // cold-start-vertraging maar een echte crash. Genormaliseerd vóór gebruik lost dit op, en
    // maakt de Azure-instelling consistent met wat het migratiehulpmiddel al accepteerde.
    internal static string BuildConnectionString(string? raw)
    {
        if (raw is null)
            throw new InvalidOperationException($"Omgevingsvariabele '{ConnectionStringEnvVar}' is niet gezet.");

        var normalized = PostgresConnectionStringNormalizer.Normalize(raw);

        // Herkenbare toepassingsnaam op elke verbinding (#863-precedent) — een onafhankelijke
        // bevestiging (bijv. via pg_stat_activity) naast wat de applicatie in /api/health over
        // zichzelf zegt.
        return new NpgsqlConnectionStringBuilder(normalized) { ApplicationName = "SportlinkFunctionAppPostgres" }.ConnectionString;
    }
}
