using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres;

/// <summary>
/// Postgres-tier-equivalent van <c>SystemUtilities.AppSettings</c> (#887) — een procesbrede,
/// statische instellingencache, gevuld vanuit <c>public.appsettings</c>.
/// <para>
/// <b>Bewust beperkt tot de kolommen die <c>public.appsettings</c> vandaag daadwerkelijk heeft</b>
/// (<c>clubcode</c>, <c>accommodatie</c>, <c>syncenabled</c> — zie
/// <c>Database.Postgres/migrations/001_baseline.sql</c>). De SQL Server-tier se
/// <c>dbo.AppSettings</c> heeft ~18 kolommen (sportlinkApiUrl, KNVB-instellingen, e-mailvoetnoot,
/// ...) — die horen bij functionaliteit die nog niet is vertaald (#889/#890 e.a.). Een fantoom-
/// fallback voor kolommen die niet bestaan zou misconfiguratie maskeren; nieuwe sub-issues breiden
/// dit uit zodra de bijbehorende Postgres-migratie de kolom toevoegt.
/// </para>
/// <para>
/// Filtert op <c>syncenabled = true</c> — zelfde precedent als
/// <see cref="Database.Postgres.PostgresPlannerViewGenerator"/>'s CROSS JOIN LATERAL: de
/// democlub (<c>syncenabled = false</c>) mag nooit stilzwijgend als primaire club gekozen worden.
/// </para>
/// </summary>
public static class PostgresAppSettings
{
    private static readonly Dictionary<string, string> Settings = new();
    private static readonly object Lock = new();

    public static async Task LoadSettingsAsync(ILogger log)
    {
        await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT clubcode, accommodatie, syncenabled FROM public.appsettings " +
            "WHERE syncenabled = true ORDER BY clubcode LIMIT 1", connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            log.LogWarning("public.appsettings heeft geen rij met syncenabled=true — instellingencache blijft leeg.");
            return;
        }

        lock (Lock)
        {
            Settings["clubCode"] = reader.GetString(0);
            if (!reader.IsDBNull(1))
                Settings["accommodatie"] = reader.GetString(1);
        }
    }

    public static string? GetSetting(string key)
    {
        lock (Lock)
            return Settings.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>Test-only: zie SystemUtilities.AppSettings.SetForTests in de SQL Server-tier voor de rationale.</summary>
    internal static void SetForTests(string key, string value)
    {
        lock (Lock)
            Settings[key] = value;
    }

    internal static void ResetForTests()
    {
        lock (Lock)
            Settings.Clear();
    }
}
