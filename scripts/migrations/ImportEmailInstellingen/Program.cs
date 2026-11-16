using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using Planner.Shared.Email;

// #1977: explicit, one-club private import. Do not run as part of migrations or deployment.
try
{
    await Importer.RunAsync(args);
}
catch (Exception)
{
    // Provider errors can contain connection strings or parameter values. Never print the exception.
    Console.Error.WriteLine("Private import mislukt; geen waarden getoond. Controleer de besloten diagnose.");
    Environment.ExitCode = 1;
}

internal static class Importer
{
internal static async Task RunAsync(string[] args)
{
    var config = LeesConfig(args);
    await ImporteerAsync(config);
}

private static ImportConfig LeesConfig(string[] args)
{
if (args.Length is not (4 or 5) || args[0] != "--tier" || args[2] != "--club-code"
    || (args.Length == 5 && args[4] != "--apply"))
    throw new InvalidOperationException("Gebruik --tier postgres|sqlserver --club-code <clubcode> [--apply].");
var tier = args[1];
var clubCode = args[3];
if (string.IsNullOrWhiteSpace(clubCode) || clubCode.Length > 20)
    throw new InvalidOperationException("Een expliciete, geldige clubcode is vereist.");
var connectionString = Environment.GetEnvironmentVariable("EMAIL_SETTINGS_IMPORT_CONNECTION_STRING");
var legacyProcessor = Environment.GetEnvironmentVariable("EmailProcessorEnabled");
var legacyReview = Environment.GetEnvironmentVariable("EmailReviewMode");
var legacyRecipient = Environment.GetEnvironmentVariable("EmailReviewRecipient");
if (string.IsNullOrWhiteSpace(connectionString) || legacyProcessor is null || legacyReview is null)
    throw new InvalidOperationException("Private bron onvolledig; import niet uitgevoerd.");
if (!EmailVerwerkingsInstellingenValidatie.TryNormaliseerOntvanger(legacyRecipient, out var ontvanger))
    throw new InvalidOperationException("Private review-ontvanger ongeldig; import niet uitgevoerd.");
if (!EmailVerwerkingsInstellingenImport.TryLeesLegacySchakelaar(legacyProcessor, out var verwerking)
    || !EmailVerwerkingsInstellingenImport.TryLeesLegacySchakelaar(legacyReview, out var review))
    throw new InvalidOperationException("Private schakelaarwaarde ongeldig; import niet uitgevoerd.");

return new ImportConfig(tier, clubCode, connectionString,
    verwerking, review, ontvanger, args.Length == 5);
}

private static async Task ImporteerAsync(ImportConfig config)
{
var tier = config.Tier;
var clubCode = config.ClubCode;
DbConnection connection = tier switch
{
    "postgres" => new NpgsqlConnection(config.ConnectionString),
    "sqlserver" => new SqlConnection(config.ConnectionString),
    _ => throw new InvalidOperationException("Onbekende databasetier.")
};
await using (connection)
{
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
    var table = tier == "postgres" ? "public.appsettings" : "[dbo].[AppSettings]";
    var filter = tier == "postgres" ? "clubcode" : "[ClubCode]";
    var columns = tier == "postgres"
        ? "emailverwerkingingeschakeld, emailreviewmodusactief, emailreviewontvanger, emailinstellingengeimporteerd, emailinstellingenbeheerdergewijzigd"
        : "[EmailVerwerkingIngeschakeld], [EmailReviewModusActief], [EmailReviewOntvanger], [EmailInstellingenGeimporteerd], [EmailInstellingenBeheerderGewijzigd]";
    var lockSql = tier == "postgres" ? " FOR UPDATE" : "";
    var from = tier == "postgres" ? table : table + " WITH (UPDLOCK, HOLDLOCK)";
    await using var read = connection.CreateCommand();
    read.Transaction = transaction;
    read.CommandText = $"SELECT {columns} FROM {from} WHERE {filter} = @clubcode{lockSql}";
    Add(read, "clubcode", clubCode);
    var huidig = await LeesHuidigAsync(read);
    if (huidig is null)
        throw new InvalidOperationException("Instellingenrij ontbreekt; import niet uitgevoerd.");
    var besluit = EmailVerwerkingsInstellingenImport.Bepaal(huidig, legacyBronBeschikbaar: true);
    if (besluit != EmailVerwerkingsInstellingenImportBeslissing.Importeren)
    {
        await transaction.RollbackAsync();
        Console.WriteLine($"Import overgeslagen: {besluit}.");
        return;
    }
    if (!config.Apply)
    {
        await transaction.RollbackAsync();
        if (config.Review && config.Ontvanger is null)
            Console.WriteLine("Review-ontvanger ontbreekt: voorstellen blijven alleen in het e-maillog.");
        Console.WriteLine("Private import kan worden toegepast op de expliciet gekozen club.");
        return;
    }
    var verwerking = config.Verwerking;
    var review = config.Review;
    var ontvanger = config.Ontvanger;
    await using var update = connection.CreateCommand();
    update.Transaction = transaction;
    update.CommandText = tier == "postgres"
        ? "UPDATE public.appsettings SET emailverwerkingingeschakeld=@processing, emailreviewmodusactief=@review, emailreviewontvanger=@recipient, emailinstellingengeimporteerd=true WHERE clubcode=@clubcode"
        : "UPDATE [dbo].[AppSettings] SET [EmailVerwerkingIngeschakeld]=@processing, [EmailReviewModusActief]=@review, [EmailReviewOntvanger]=@recipient, [EmailInstellingenGeimporteerd]=1 WHERE [ClubCode]=@clubcode";
    Add(update, "processing", verwerking);
    Add(update, "review", review);
    Add(update, "recipient", (object?)ontvanger ?? DBNull.Value);
    Add(update, "clubcode", clubCode);
    if (await update.ExecuteNonQueryAsync() != 1)
        throw new InvalidOperationException("Clubrij wijzigde tijdens import; transactie afgebroken.");
    await SchrijfAuditAsync(connection, transaction, tier, clubCode, huidig, verwerking, review, ontvanger);
    await transaction.CommitAsync();
    if (config.Review && config.Ontvanger is null)
        Console.WriteLine("Review-ontvanger ontbreekt: voorstellen blijven alleen in het e-maillog.");
    Console.WriteLine("Private import voltooid voor de expliciet gekozen club.");
}
}

private static async Task<EmailVerwerkingsInstellingen?> LeesHuidigAsync(DbCommand command)
{
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return null;
    return new(reader.GetBoolean(0), reader.GetBoolean(1),
        reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4));
}

private static async Task SchrijfAuditAsync(DbConnection connection, DbTransaction transaction,
    string tier, string clubCode, EmailVerwerkingsInstellingen huidig,
    bool verwerking, bool review, string? ontvanger)
{
    var auditTable = tier == "postgres" ? "public.appsettingsaudit" : "[dbo].[AppSettingsAudit]";
    var auditColumns = tier == "postgres"
        ? "gewijzigddoor, veld, oudewaarde, nieuwewaarde, clubcode"
        : "[GewijzigdDoor], [Veld], [OudeWaarde], [NieuweWaarde], [ClubCode]";
    foreach (var (veld, oud, nieuw) in new (string, string?, string?)[]
    {
        ("EmailVerwerkingIngeschakeld", huidig.VerwerkingIngeschakeld.ToString(), verwerking.ToString()),
        ("EmailReviewModusActief", huidig.ReviewModusActief.ToString(), review.ToString()),
        ("EmailReviewOntvanger", huidig.ReviewOntvanger, ontvanger)
    })
    {
        var veilig = EmailVerwerkingsInstellingenWijziging.AuditWaarden(veld, oud, nieuw);
        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = $"INSERT INTO {auditTable} ({auditColumns}) VALUES (@actor,@field,@old,@new,@clubcode)";
        Add(audit, "actor", "private-import");
        Add(audit, "field", veld);
        Add(audit, "old", (object?)veilig.OudeWaarde ?? DBNull.Value);
        Add(audit, "new", (object?)veilig.NieuweWaarde ?? DBNull.Value);
        Add(audit, "clubcode", clubCode);
        await audit.ExecuteNonQueryAsync();
    }
}

private static void Add(DbCommand command, string name, object value)
{
    var parameter = command.CreateParameter();
    parameter.ParameterName = name;
    parameter.Value = value;
    command.Parameters.Add(parameter);
}

private sealed class ImportConfig(
    string tier, string clubCode, string connectionString, bool verwerking, bool review,
    string? ontvanger, bool apply)
{
    public string Tier { get; } = tier;
    public string ClubCode { get; } = clubCode;
    public string ConnectionString { get; } = connectionString;
    public bool Verwerking { get; } = verwerking;
    public bool Review { get; } = review;
    public string? Ontvanger { get; } = ontvanger;
    public bool Apply { get; } = apply;
    public override string ToString() => "[private email import configuration]";
}
}
