using Microsoft.Data.SqlClient;
using Npgsql;
using SqlServerToPostgresCopy;

// #976: eenmalige productiecutover-tool. Connectiestrings en clubcode komen uitsluitend via
// omgevingsvariabelen binnen — nooit als CLI-argument (zelfde regel als overal elders in dit
// project: argumenten staan op elk platform zichtbaar in de processenlijst).
//
// Gebruik:
//   SQLSERVER_CONNECTION_STRING=... POSTGRES_CONNECTION_STRING=... PRODUCTIE_CLUBCODE=...
//     dotnet run --project MigrationTools/SqlServerToPostgresCopy -- --dry-run
//     dotnet run --project MigrationTools/SqlServerToPostgresCopy --                 (voert echt uit)
//
// --dry-run telt alleen rijen aan beide kanten (geen schrijfactie) — altijd eerst zo draaien.

var sourceConnectionString = Environment.GetEnvironmentVariable("SQLSERVER_CONNECTION_STRING");
var targetConnectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
var productionClubCode = Environment.GetEnvironmentVariable("PRODUCTIE_CLUBCODE");
var dryRun = args.Contains("--dry-run");

if (string.IsNullOrWhiteSpace(sourceConnectionString))
{
    Console.Error.WriteLine("Fout: omgevingsvariabele SQLSERVER_CONNECTION_STRING is niet gezet.");
    return 1;
}
if (string.IsNullOrWhiteSpace(targetConnectionString))
{
    Console.Error.WriteLine("Fout: omgevingsvariabele POSTGRES_CONNECTION_STRING is niet gezet.");
    return 1;
}
if (string.IsNullOrWhiteSpace(productionClubCode))
{
    Console.Error.WriteLine("Fout: omgevingsvariabele PRODUCTIE_CLUBCODE is niet gezet.");
    return 1;
}
if (productionClubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Fout: PRODUCTIE_CLUBCODE mag niet 'ALLSTARS' zijn — dat is de democlub.");
    return 1;
}

// Alle tabellen die uitsluitend lokaal ingevoerde configuratie/geleerde status bevatten — his/stg
// (ETL-data uit de Sportlink-sync) horen hier expliciet NIET bij: die worden dynamisch beheerd
// door PostgresMergeOrchestrator en kunnen na cutover via een nieuwe sync (desnoods met
// ?reset=true&season=YYYY per seizoen) opnieuw gevuld worden. Zie issue #976 voor de volledige
// onderbouwing van deze afbakening.
//
// VOLGORDE DOET ERTOE voor drie tabellen met IdentityMapKey (Teams, VeldPeriode, EmailVerwerking):
// die moeten vóór de tabel staan die hun (opnieuw gegenereerde) id via ForeignKeyRemaps overneemt
// — anders faalt IdMapRegistry.Translate met "geen nieuwe id gevonden". Zie TableCopier.cs voor
// waarom identity-waarden nooit letterlijk gekopieerd worden (AllStars-democlubdata-botsing).
var tables = new List<TableMapping>
{
    new("dbo", "AppSettings", "public", "appsettings", HasClubCode: true),
    new("dbo", "AppSettingsAudit", "public", "appsettingsaudit", HasClubCode: true),
    new("dbo", "Velden", "public", "velden", HasClubCode: true),
    new("dbo", "VeldPeriode", "public", "veldperiode", HasClubCode: true, IdentityMapKey: "veldperiode"),
    new("dbo", "VeldBeschikbaarheid", "public", "veldbeschikbaarheid", HasClubCode: true,
        ForeignKeyRemaps: new Dictionary<string, string> { ["periodeid"] = "veldperiode" }),
    new("dbo", "VeldTraining", "public", "veldtraining", HasClubCode: true),
    new("dbo", "Speeltijden", "public", "speeltijden", HasClubCode: true),
    new("dbo", "TeamVoorkeurTijden", "public", "teamvoorkeurtijden", HasClubCode: true),
    new("dbo", "TeamRegels", "public", "teamregels", HasClubCode: true),
    new("dbo", "UitgeslotenEmailAdressen", "public", "uitgeslotenemailadressen", HasClubCode: true),
    new("dbo", "EmailTemplateInstellingen", "public", "emailtemplateinstellingen", HasClubCode: true),
    new("dbo", "Season", "public", "season", HasClubCode: false),
    new("dbo", "Teams", "public", "teams", HasClubCode: true, IdentityMapKey: "teams"),
    new("dbo", "TeamAliassen", "public", "teamaliassen", HasClubCode: true,
        ForeignKeyRemaps: new Dictionary<string, string> { ["teamid"] = "teams" }),
    new("avg", "Teambegeleiding", "avg", "teambegeleiding", HasClubCode: true),
    new("avg", "ImportLog", "avg", "importlog", HasClubCode: true),
    new("planner", "EmailVerwerking", "planner", "emailverwerking", HasClubCode: true, IdentityMapKey: "emailverwerking"),
    new("planner", "ClassificatieCorrectie", "planner", "classificatiecorrectie", HasClubCode: true,
        ForeignKeyRemaps: new Dictionary<string, string>
        {
            ["origineleverwerkingid"] = "emailverwerking",
            ["correctionverwerkingid"] = "emailverwerking",
        }),
    new("planner", "GeplandeWedstrijden", "planner", "geplandewedstrijden", HasClubCode: true),
    new("planner", "HerplanVerzoeken", "planner", "herplanverzoeken", HasClubCode: true),
};

await using var source = new SqlConnection(sourceConnectionString);
await using var target = new NpgsqlConnection(NormalizePostgresConnectionString(targetConnectionString));
await source.OpenAsync();
await target.OpenAsync();

Console.WriteLine(dryRun
    ? $"DRY-RUN — telt rijen voor club '{productionClubCode}', schrijft niets."
    : $"LIVE-KOPIE — schrijft naar Postgres voor club '{productionClubCode}'.");
Console.WriteLine();

var idMaps = new IdMapRegistry();
var results = new List<TableCopyResult>();
foreach (var mapping in tables)
{
    Console.WriteLine($"{mapping.SourceSchema}.{mapping.SourceTable} -> {mapping.TargetSchema}.{mapping.TargetTable}");
    try
    {
        var result = await TableCopier.CopyAsync(
            source, target, mapping, productionClubCode, dryRun, idMaps, Console.Out, CancellationToken.None);
        results.Add(result);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  FOUT bij {mapping.TargetTable}: {ex.Message}");
        return 1;
    }
    Console.WriteLine();
}

if (!dryRun)
{
    Console.WriteLine("=== Verificatie (bron-rijtelling vs. gekopieerd) ===");
    var mismatches = results.Where(r => !r.RowCountsMatch).ToList();
    foreach (var r in results)
        Console.WriteLine($"  {(r.RowCountsMatch ? "OK  " : "FOUT")} {r.TargetTable}: bron={r.SourceRowCount} gekopieerd={r.CopiedRowCount}");

    if (mismatches.Count > 0)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"{mismatches.Count} tabel(len) met een rijtelling-mismatch — niet zonder onderzoek als geslaagd beschouwen.");
        return 1;
    }
}

Console.WriteLine();
Console.WriteLine("Klaar.");
return 0;

// Supabase's dashboard toont twee vormen naast elkaar: een "Connection string" (URI, bijv.
// postgresql://postgres.<ref>:<wachtwoord>@<host>:5432/postgres) en "Connection parameters"
// (keyword=value, Npgsql's eigen vorm). Beide zijn een voor de hand liggende keuze om te
// kopiëren, en Npgsql accepteert alleen de tweede rechtstreeks — vandaar deze normalisatie in
// plaats van uitsluitend documentatie die op één vorm aandringt.
static string NormalizePostgresConnectionString(string raw)
{
    if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        return raw;

    var uri = new Uri(raw);
    var userInfo = uri.UserInfo.Split(':', 2);
    var username = Uri.UnescapeDataString(userInfo[0]);
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
    var database = uri.AbsolutePath.TrimStart('/');
    var port = uri.Port > 0 ? uri.Port : 5432;

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = port,
        Database = string.IsNullOrEmpty(database) ? "postgres" : database,
        Username = username,
        Password = password,
        SslMode = SslMode.Require,
    };
    return builder.ConnectionString;
}
