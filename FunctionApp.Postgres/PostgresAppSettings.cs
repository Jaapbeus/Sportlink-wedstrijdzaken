using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres;

/// <summary>
/// Postgres-tier-equivalent van <c>SystemUtilities.AppSettings</c> (#887) — een procesbrede,
/// statische instellingencache, gevuld vanuit <c>public.appsettings</c>.
/// <para>
/// <b>Bewust beperkt tot de kolommen die <c>public.appsettings</c> vandaag daadwerkelijk heeft</b>
/// (<c>clubcode</c>, <c>accommodatie</c>, <c>syncenabled</c> — zie
/// <c>Database.Postgres/migrations/001_baseline.sql</c> — en sinds issue 888 vervolg/§41 ook
/// <c>accommodatielatitude</c>/<c>accommodatielongitude</c>, nodig voor
/// <c>PostgresSunsetCalculator</c>, uit <c>003_admin_tables.sql</c>). De SQL Server-tier se
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

    // #859: zichtbaar maken voor HealthFunction dat de cache leeg/verouderd is, in plaats van
    // alleen een logregel — zelfde signaal als SystemUtilities.AppSettings.LastLoadFailed op de
    // SQL Server-tier. Gooit hierbeneden nog steeds door: WaitForDatabaseAsync's retry-lus moet
    // een echte databasefout hier blijven zien als reden om opnieuw te proberen.
    public static bool LastLoadFailed { get; private set; }

    public static async Task LoadSettingsAsync(ILogger log)
    {
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            // accommodatielatitude/-longitude erbij (issue 888 vervolg, §41): PostgresSunsetCalculator
            // heeft dezelfde clubinstellingen nodig als SunsetCalculator op de SQL Server-tier.
            // clubname erbij (#889): BerichtAiService's classificatie-systemprompt noemt de clubnaam
            // ("Je bent een assistent voor de coördinator thuiswedstrijden van {clubNaam}") — zonder
            // deze kolom gooit die prompt-opbouw een InvalidOperationException.
            await using var cmd = new NpgsqlCommand(
                "SELECT clubcode, accommodatie, syncenabled, accommodatielatitude, accommodatielongitude, plannerafzendernaam, clubname FROM public.appsettings " +
                "WHERE syncenabled = true ORDER BY clubcode LIMIT 1", connection);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                log.LogWarning("public.appsettings heeft geen rij met syncenabled=true — instellingencache blijft leeg.");
                LastLoadFailed = true;
                return;
            }

            lock (Lock)
            {
                Settings["clubCode"] = reader.GetString(0);
                if (!reader.IsDBNull(1))
                    Settings["accommodatie"] = reader.GetString(1);
                if (!reader.IsDBNull(3))
                    Settings["accommodatieLatitude"] = reader.GetDouble(3).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!reader.IsDBNull(4))
                    Settings["accommodatieLongitude"] = reader.GetDouble(4).ToString(System.Globalization.CultureInfo.InvariantCulture);
                // plannerafzendernaam (§42): AutoPlan zet deze naam onder de gegenereerde HTML-planning.
                if (!reader.IsDBNull(5))
                    Settings["plannerAfzenderNaam"] = reader.GetString(5);
                // clubname (#889): zie de aanroep hierboven.
                if (!reader.IsDBNull(6))
                    Settings["clubName"] = reader.GetString(6);
            }
            LastLoadFailed = false;
        }
        catch
        {
            LastLoadFailed = true;
            throw;
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
        LastLoadFailed = false;
    }
}
