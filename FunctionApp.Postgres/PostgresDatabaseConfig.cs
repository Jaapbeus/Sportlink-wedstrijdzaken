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

    public static string ConnectionString { get; } = BuildConnectionString();

    private static string BuildConnectionString()
    {
        var raw = Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
            ?? throw new InvalidOperationException($"Omgevingsvariabele '{ConnectionStringEnvVar}' is niet gezet.");

        // Herkenbare toepassingsnaam op elke verbinding (#863-precedent) — een onafhankelijke
        // bevestiging (bijv. via pg_stat_activity) naast wat de applicatie in /api/health over
        // zichzelf zegt.
        return new NpgsqlConnectionStringBuilder(raw) { ApplicationName = "SportlinkFunctionAppPostgres" }.ConnectionString;
    }
}
