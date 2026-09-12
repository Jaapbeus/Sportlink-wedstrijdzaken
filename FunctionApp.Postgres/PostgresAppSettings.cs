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
/// <b>Eén bewuste uitzondering op die regel (#1098):</b> <c>sportlinkextensionenabled</c> (migratie
/// 012). Op de Postgres-tier past niets de migraties automatisch toe op productie — dat is een
/// handmatige stap van de eigenaar (ARCHITECTUUR-DATABASE-TIERS.md §49). Release v3.3.0.0 leverde
/// deze query met die kolom, terwijl migratie 012 in productie nog niet gedraaid had: <c>42703
/// undefined_column</c> → <c>WaitForDatabaseAsync</c> zag "database onbereikbaar" → elk
/// <c>/api/beheer/*</c>-endpoint 500 "Ophalen mislukt". Ontbreekt die kolom, dan valt de lader nu
/// terug op de kolomset van v3.2 en geldt <c>sportlinkExtensionEnabled = "0"</c> — exact de
/// <c>DEFAULT false</c> die migratie 012 zelf zou zetten. Niet stil: <see cref="SchemaWarning"/>
/// staat in <c>/api/health</c> en het functielog meldt het één keer per proces. Zelfde
/// "melden, niet weigeren"-lijn als #1095.
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
    private static int _schemaWarningLogged;

    // #859: zichtbaar maken voor HealthFunction dat de cache leeg/verouderd is, in plaats van
    // alleen een logregel — zelfde signaal als SystemUtilities.AppSettings.LastLoadFailed op de
    // SQL Server-tier. Gooit hierbeneden nog steeds door: WaitForDatabaseAsync's retry-lus moet
    // een echte databasefout hier blijven zien als reden om opnieuw te proberen.
    public static bool LastLoadFailed { get; private set; }

    /// <summary>
    /// #1098: <c>null</c> als <c>public.appsettings</c> alle kolommen heeft die deze versie
    /// verwacht; anders een waarschuwing (zonder host of credentials) dat de code vooruitloopt op
    /// het databaseschema en welke migratie ontbreekt. Bedoeld voor <c>/api/health</c>.
    /// </summary>
    public static string? SchemaWarning { get; private set; }

    /// <summary>
    /// #1098: <c>false</c> zolang de laatste laadpoging <see cref="ExtensionColumn"/> niet in
    /// <c>public.appsettings</c> aantrof. <c>AdminSettingsFunction</c> leest dit om dezelfde kolom
    /// niet zelf opnieuw te selecteren (GET) en een poging de extensie in te schakelen vóór de
    /// migratie met een duidelijke 409 te beantwoorden (PUT) in plaats van een generieke 500.
    /// </summary>
    public static bool ExtensionColumnAvailable => SchemaWarning is null;

    internal const string ExtensionColumn = "sportlinkextensionenabled";

    private const string BaseColumns =
        "clubcode, accommodatie, syncenabled, accommodatielatitude, accommodatielongitude, plannerafzendernaam, clubname";

    private const string ExtensionColumnMissingWarning =
        "Kolom '" + ExtensionColumn + "' ontbreekt in public.appsettings: migratie " +
        "012_sportlink_extension.sql is niet toegepast op deze database. De Sportlink Web Extension " +
        "geldt als uitgeschakeld tot de openstaande migraties zijn uitgevoerd (Database.Postgres.Cli) — " +
        "zie 'pendingMigrations' in /api/health en docs/ARCHITECTUUR-DATABASE-TIERS.md §54 (#1098).";

    public static async Task LoadSettingsAsync(ILogger log)
    {
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            bool gevonden;
            try
            {
                gevonden = await ReadAsync(connection, includeExtensionColumn: true);
                SchemaWarning = null;
            }
            catch (PostgresException ex) when (IsOntbrekendeKolom(ex, ExtensionColumn))
            {
                // #1098: zie de klassedocumentatie. Alleen dít specifieke 42703-geval — elke andere
                // ontbrekende kolom blijft een harde fout, want daar is geen migratie-default voor.
                SchemaWarning = ExtensionColumnMissingWarning;
                if (Interlocked.Exchange(ref _schemaWarningLogged, 1) == 0)
                    log.LogWarning("{SchemaWarning}", ExtensionColumnMissingWarning);
                gevonden = await ReadAsync(connection, includeExtensionColumn: false);
            }

            if (!gevonden)
            {
                log.LogWarning("public.appsettings heeft geen rij met syncenabled=true — instellingencache blijft leeg.");
                LastLoadFailed = true;
                return;
            }

            LastLoadFailed = false;
        }
        catch
        {
            LastLoadFailed = true;
            throw;
        }
    }

    /// <summary>
    /// Leest de primaire club en vult de cache. <c>false</c> als er geen rij met
    /// <c>syncenabled = true</c> is. Bij <paramref name="includeExtensionColumn"/> = false wordt
    /// <c>sportlinkExtensionEnabled</c> op "0" gezet — de default van migratie 012.
    /// </summary>
    private static async Task<bool> ReadAsync(NpgsqlConnection connection, bool includeExtensionColumn)
    {
        // accommodatielatitude/-longitude erbij (issue 888 vervolg, §41): PostgresSunsetCalculator
        // heeft dezelfde clubinstellingen nodig als SunsetCalculator op de SQL Server-tier.
        // clubname erbij (#889): BerichtAiService's classificatie-systemprompt noemt de clubnaam
        // ("Je bent een assistent voor de coördinator thuiswedstrijden van {clubNaam}") — zonder
        // deze kolom gooit die prompt-opbouw een InvalidOperationException.
        // sportlinkextensionenabled erbij (#988): Sportlink Web Extension-schakelaar, standaard false.
        var kolommen = includeExtensionColumn ? BaseColumns + ", " + ExtensionColumn : BaseColumns;
        await using var cmd = new NpgsqlCommand(
            $"SELECT {kolommen} FROM public.appsettings WHERE syncenabled = true ORDER BY clubcode LIMIT 1", connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return false;

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
            Settings["sportlinkExtensionEnabled"] =
                includeExtensionColumn && !reader.IsDBNull(7) && reader.GetBoolean(7) ? "1" : "0";
        }
        return true;
    }

    /// <summary>
    /// Herkent Postgres' <c>42703 undefined_column</c> voor precies één kolom. De kolomnaam staat
    /// in de fouttekst tussen dubbele aanhalingstekens (<c>column "x" does not exist</c>) — dat
    /// voorkomt dat een andere ontbrekende kolom per ongeluk als dit geval doorgaat.
    /// </summary>
    internal static bool IsOntbrekendeKolom(PostgresException ex, string kolom)
        => ex.SqlState == PostgresErrorCodes.UndefinedColumn
           && ex.MessageText.Contains($"\"{kolom}\"", StringComparison.Ordinal);

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
        SchemaWarning = null;
        _schemaWarningLogged = 0;
    }
}
