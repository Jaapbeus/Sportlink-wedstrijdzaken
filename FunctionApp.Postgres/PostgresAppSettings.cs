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
/// <c>PostgresSunsetCalculator</c>, uit <c>003_admin_tables.sql</c>; sinds #1141 ook
/// <c>knvbpdfbijlageingeschakeld</c>/<c>knvbstandaardregio</c>, nodig voor het "verzet zonder
/// datum"-pad, eveneens uit <c>003_admin_tables.sql</c>). De SQL Server-tier se
/// <c>dbo.AppSettings</c> heeft ~18 kolommen (sportlinkApiUrl, e-mailvoetnoot, ...) — die horen bij
/// functionaliteit die nog niet is vertaald (#889/#890 e.a.). Een fantoom-fallback voor kolommen
/// die niet bestaan zou misconfiguratie maskeren; nieuwe sub-issues breiden dit uit zodra de
/// bijbehorende Postgres-migratie de kolom toevoegt.
/// </para>
/// <para>
/// <b>Twee bewuste uitzonderingen op die regel — telkens een optionele, losstaande kolom
/// (#1098, #998):</b> <c>sportlinkextensionenabled</c> (migratie 012) en <c>sportlinkdryrun</c>
/// (migratie 016). Op de Postgres-tier past niets de migraties automatisch toe op productie — dat
/// is een handmatige stap van de eigenaar (ARCHITECTUUR-DATABASE-TIERS.md §49). Release v3.3.0.0
/// leverde de query met de eerste kolom terwijl migratie 012 in productie nog niet gedraaid had:
/// <c>42703 undefined_column</c> → <c>WaitForDatabaseAsync</c> zag "database onbereikbaar" → elk
/// <c>/api/beheer/*</c>-endpoint 500 "Ophalen mislukt". Beide kolommen worden daarom onafhankelijk
/// van elkaar geprobeerd; ontbreekt er één, dan valt <see cref="ReadAsync"/> voor precies die kolom
/// terug op de migratie-default:
/// <list type="bullet">
/// <item><c>sportlinkExtensionEnabled = "0"</c> — <c>DEFAULT false</c> uit migratie 012.</item>
/// <item><c>sportlinkDryRun = "1"</c> — <c>DEFAULT true</c> uit migratie 016. Bewust fail-safe:
/// een ontbrekende kolom mag nooit stilzwijgend live Sportlink-mutaties toestaan.</item>
/// </list>
/// Niet stil: <see cref="SchemaWarning"/> staat in <c>/api/health</c> en het functielog meldt het
/// één keer per proces. Zelfde "melden, niet weigeren"-lijn als #1095.
/// </para>
/// <para>
/// Filtert op <c>syncenabled = true</c> — zelfde precedent als
/// <see cref="Database.Postgres.PostgresPlannerViewGenerator"/>'s CROSS JOIN LATERAL: de
/// democlub (<c>syncenabled = false</c>) mag nooit stilzwijgend als primaire club gekozen worden.
/// </para>
/// </summary>
public static class PostgresAppSettings
{
    // #1135: NIET readonly — een geslaagde herlaad bouwt een volledig verse snapshot op in ReadAsync
    // en wisselt daarna deze referentie in één keer om (onder Lock). Vóór #1135 was dit een readonly
    // dictionary dat ReadAsync in-place muteerde en alleen niet-NULL kolommen toewees: een instelling
    // die in de database naar NULL werd gezet, bleef daardoor de oude waarde teruggeven totdat het
    // proces herstartte, want er was niets dat de oude sleutel ooit verwijderde.
    private static Dictionary<string, string> Settings = new();
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
    public static bool ExtensionColumnAvailable { get; private set; } = true;

    /// <summary>
    /// #998: zelfde precedent als <see cref="ExtensionColumnAvailable"/>, maar voor
    /// <see cref="DryRunColumn"/> (migratie 016).
    /// </summary>
    public static bool DryRunColumnAvailable { get; private set; } = true;

    internal const string ExtensionColumn = "sportlinkextensionenabled";
    internal const string DryRunColumn = "sportlinkdryrun";

    private const string BaseColumns =
        "clubcode, accommodatie, syncenabled, accommodatielatitude, accommodatielongitude, plannerafzendernaam, clubname, " +
        "knvbpdfbijlageingeschakeld, knvbstandaardregio";

    private const string ExtensionColumnMissingWarning =
        "Kolom '" + ExtensionColumn + "' ontbreekt in public.appsettings: migratie " +
        "012_sportlink_extension.sql is niet toegepast op deze database. De Sportlink Web Extension " +
        "geldt als uitgeschakeld tot de openstaande migraties zijn uitgevoerd (Database.Postgres.Cli) — " +
        "zie 'pendingMigrations' in /api/health en docs/ARCHITECTUUR-DATABASE-TIERS.md §55 (#1098).";

    private const string DryRunColumnMissingWarning =
        "Kolom '" + DryRunColumn + "' ontbreekt in public.appsettings: migratie " +
        "016_sportlink_dryrun_en_contractcheck.sql is niet toegepast op deze database. De dry-run-modus " +
        "geldt als AAN (fail-safe, geen live Sportlink-mutaties) tot de openstaande migraties zijn " +
        "uitgevoerd (Database.Postgres.Cli) — zie 'pendingMigrations' in /api/health en " +
        "docs/ARCHITECTUUR-DATABASE-TIERS.md §55 (#998).";

    public static async Task LoadSettingsAsync(ILogger log)
    {
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            // #1098/#998: beide optionele kolommen worden onafhankelijk van elkaar geprobeerd —
            // migratie 012 en 016 kunnen elk apart nog niet zijn toegepast. Alleen dít specifieke
            // 42703-geval per kolom triggert een retry; elke andere ontbrekende kolom blijft een
            // harde fout, want daar is geen migratie-default voor.
            var includeExtensionColumn = true;
            var includeDryRunColumn = true;
            bool gevonden;
            while (true)
            {
                try
                {
                    gevonden = await ReadAsync(connection, includeExtensionColumn, includeDryRunColumn);
                    break;
                }
                catch (PostgresException ex) when (includeExtensionColumn && IsOntbrekendeKolom(ex, ExtensionColumn))
                {
                    includeExtensionColumn = false;
                }
                catch (PostgresException ex) when (includeDryRunColumn && IsOntbrekendeKolom(ex, DryRunColumn))
                {
                    includeDryRunColumn = false;
                }
            }

            ExtensionColumnAvailable = includeExtensionColumn;
            DryRunColumnAvailable = includeDryRunColumn;
            UpdateSchemaWarning(log, includeExtensionColumn, includeDryRunColumn);

            if (!gevonden)
            {
                // #1135: bewuste, expliciete keuze — "geen enkele club met syncenabled=true" is een
                // aparte toestand van "een geslaagde load die velden heeft gewist" (het geval dat de
                // rest van deze methode fixt). Het is geen signaal om de bestaande cache leeg te
                // vegen: bestaande callers (AdminSettingsFunction, PostgresSunsetCalculator, de
                // Sportlink-mutatiepijplijn) verwachten dat GetSetting tijdens zo'n overgangs- of
                // configuratiefout niet ineens overal null teruggeeft. LastLoadFailed=true blijft
                // het signaal dat de laatste laadpoging niet vertrouwd kan worden (zie
                // WaitForDatabaseAsync/HealthFunction) — een load-fout wordt dus nooit stilzwijgend
                // behandeld als een geslaagde wis-actie.
                log.LogWarning("public.appsettings heeft geen rij met syncenabled=true — instellingencache blijft ongewijzigd (mogelijk verouderd).");
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

    private static void UpdateSchemaWarning(ILogger log, bool extensionColumnAvailable, bool dryRunColumnAvailable)
    {
        var warnings = new List<string>();
        if (!extensionColumnAvailable) warnings.Add(ExtensionColumnMissingWarning);
        if (!dryRunColumnAvailable) warnings.Add(DryRunColumnMissingWarning);

        SchemaWarning = warnings.Count == 0 ? null : string.Join(" ", warnings);
        if (warnings.Count > 0 && Interlocked.Exchange(ref _schemaWarningLogged, 1) == 0)
            log.LogWarning("{SchemaWarning}", SchemaWarning);
    }

    /// <summary>
    /// Leest de primaire club en vult de cache. <c>false</c> als er geen rij met
    /// <c>syncenabled = true</c> is. Bij <paramref name="includeExtensionColumn"/> = false wordt
    /// <c>sportlinkExtensionEnabled</c> op "0" gezet — de default van migratie 012. Bij
    /// <paramref name="includeDryRunColumn"/> = false wordt <c>sportlinkDryRun</c> op "1" gezet —
    /// de default van migratie 016 (fail-safe: dry-run AAN).
    /// </summary>
    private static async Task<bool> ReadAsync(NpgsqlConnection connection, bool includeExtensionColumn, bool includeDryRunColumn)
    {
        // accommodatielatitude/-longitude erbij (issue 888 vervolg, §41): PostgresSunsetCalculator
        // heeft dezelfde clubinstellingen nodig als SunsetCalculator op de SQL Server-tier.
        // clubname erbij (#889): BerichtAiService's classificatie-systemprompt noemt de clubnaam
        // ("Je bent een assistent voor de coördinator thuiswedstrijden van {clubNaam}") — zonder
        // deze kolom gooit die prompt-opbouw een InvalidOperationException.
        // sportlinkextensionenabled erbij (#988): Sportlink Web Extension-schakelaar, standaard false.
        // sportlinkdryrun erbij (#998): dry-run-schakelaar voor SportlinkClubClient.PutMutationAsync
        // — gelezen bij ELKE mutatie-aanroep via de Func<bool>-delegate in Program.cs, dus de
        // Instellingen-toggle heeft direct effect zodra deze cache ververst is (LoadSettingsAsync
        // wordt na elke AdminSettingsPut opnieuw aangeroepen). Kolomnamen worden via GetOrdinal
        // opgezocht in plaats van vaste posities: welke van de twee optionele kolommen aanwezig is
        // varieert onafhankelijk van elkaar.
        var kolommen = BaseColumns;
        if (includeExtensionColumn) kolommen += ", " + ExtensionColumn;
        if (includeDryRunColumn) kolommen += ", " + DryRunColumn;
        await using var cmd = new NpgsqlCommand(
            $"SELECT {kolommen} FROM public.appsettings WHERE syncenabled = true ORDER BY clubcode LIMIT 1", connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return false;

        // #1135: een complete, verse snapshot opbouwen in een lokale dictionary — niet het bestaande,
        // gedeelde dictionary in-place muteren. De oude aanpak wees alleen niet-NULL kolommen toe,
        // waardoor een instelling die in de database naar NULL werd gezet (bijv. accommodatie) haar
        // vorige waarde bleef teruggeven: er was geen enkele stap die de oude sleutel ooit verwijderde.
        // Nu ontbreekt een NULL-kolom in `nieuw` gewoon volledig, en GetSetting geeft er null voor
        // terug zodra de referentie hieronder is omgewisseld.
        var nieuw = new Dictionary<string, string>
        {
            ["clubCode"] = reader.GetString(0)
        };
        if (!reader.IsDBNull(1))
            nieuw["accommodatie"] = reader.GetString(1);
        if (!reader.IsDBNull(3))
            nieuw["accommodatieLatitude"] = reader.GetDouble(3).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!reader.IsDBNull(4))
            nieuw["accommodatieLongitude"] = reader.GetDouble(4).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // plannerafzendernaam (§42): AutoPlan zet deze naam onder de gegenereerde HTML-planning.
        if (!reader.IsDBNull(5))
            nieuw["plannerAfzenderNaam"] = reader.GetString(5);
        // clubname (#889): zie de aanroep hierboven.
        if (!reader.IsDBNull(6))
            nieuw["clubName"] = reader.GetString(6);
        // knvbpdfbijlageingeschakeld/knvbstandaardregio (#1141): het "verzet zonder datum"-pad
        // (BerichtPipeline) leest deze via GetSetting zodra er geen ClubAppSettingsSnapshot is
        // (de echte, mailbox-getriggerde verwerking — niet het dry-run pad). Beide kolommen
        // bestaan onvoorwaardelijk sinds migratie 003, dus geen optionele-kolom-dans zoals bij
        // ExtensionColumn/DryRunColumn hierboven nodig.
        nieuw["knvbPdfBijlageIngeschakeld"] = !reader.IsDBNull(7) && reader.GetBoolean(7) ? "1" : "0";
        if (!reader.IsDBNull(8))
            nieuw["knvbStandaardRegio"] = reader.GetString(8);

        if (includeExtensionColumn)
        {
            var ordinal = reader.GetOrdinal(ExtensionColumn);
            nieuw["sportlinkExtensionEnabled"] = !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal) ? "1" : "0";
        }
        else
        {
            nieuw["sportlinkExtensionEnabled"] = "0";
        }

        if (includeDryRunColumn)
        {
            var ordinal = reader.GetOrdinal(DryRunColumn);
            nieuw["sportlinkDryRun"] = !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal) ? "1" : "0";
        }
        else
        {
            nieuw["sportlinkDryRun"] = "1";
        }

        // Atomaire wissel: elke lezer via GetSetting ziet óf de volledig oude, óf de volledig nieuwe
        // snapshot — nooit een tussentoestand met een deel oude en een deel nieuwe waarden.
        lock (Lock)
            Settings = nieuw;

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
        ExtensionColumnAvailable = true;
        DryRunColumnAvailable = true;
    }
}
