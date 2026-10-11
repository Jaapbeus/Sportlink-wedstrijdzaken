using Planner.Shared;
using Microsoft.Extensions.Logging;
using SportlinkFunction.Monitoring;

namespace SportlinkFunction.Email;

public partial class EmailProcessorFunction
{
    /// <summary>
    /// Reageert op een geslaagde databaseverbinding: als er een noodmail-registratie openstaat van een
    /// eerdere uitval, wordt die gewist zodat een volgende, nieuwe uitval weer een verse melding
    /// oplevert in plaats van stil te blijven omdat de oude registratie nog "openstond" (#831).
    /// </summary>
    internal static async Task BehandelDatabaseHerstelAsync(INoodmailThrottleStore throttleStore, ILogger log)
    {
        if (await throttleStore.LaatsteKeerVerstuurdAsync(DatabaseNoodmailSleutel) is not null)
        {
            await throttleStore.WisAsync(DatabaseNoodmailSleutel);
            log.LogInformation("Database weer bereikbaar — email processor hervat");
        }
    }

    /// <summary>
    /// Reageert op een mislukte databaseverbinding: stuurt een noodmail, tenzij er al één openstaat
    /// voor deze uitval. De registratie staat in <see cref="INoodmailThrottleStore"/> — niet in een
    /// static veld — zodat het gedrag niet afhangt van cold starts (#831).
    /// </summary>
    internal static async Task BehandelDatabaseVerbindingsFoutAsync(
        Exception dbEx, IEmailGraphService graphService, int aantalOnverwerkt,
        INoodmailThrottleStore throttleStore, ILogger log)
    {
        if (await throttleStore.LaatsteKeerVerstuurdAsync(DatabaseNoodmailSleutel) is null)
        {
            log.LogError(dbEx, "Database niet beschikbaar — stuur noodmail");
            await StuurDatabaseNoodmailAsync(graphService, aantalOnverwerkt, CategorizeerFout(dbEx), throttleStore, log);
        }
        else
        {
            log.LogWarning("Email processor gepauzeerd — database nog niet bereikbaar (noodmail al verstuurd)");
        }
    }

    /// <summary>
    /// Stuurt een noodmail als de database niet beschikbaar is.
    /// Emails blijven ongelezen in de inbox en worden bij de volgende poll opnieuw opgepikt.
    /// </summary>
    internal static async Task StuurDatabaseNoodmailAsync(
        IEmailGraphService graphService, int aantalEmails, string foutmelding,
        INoodmailThrottleStore throttleStore, ILogger log)
    {
        var mailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById(BerichtResponseGenerator.NlTijdzoneId);
        var nlTijd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nlZone);

        var body = $"URGENT: De database is niet bereikbaar.\n\n"
                 + $"Tijdstip: {nlTijd:dd-MM-yyyy HH:mm}\n"
                 + $"Foutmelding: {foutmelding}\n"
                 + $"Onverwerkte emails: {aantalEmails}\n\n"
                 + "De email-processor is automatisch GEPAUZEERD. Er worden geen herhaalde meldingen verstuurd.\n"
                 + "De processor hervat automatisch zodra de database weer bereikbaar is.\n\n"
                 + "De emails blijven ongelezen in de inbox en worden automatisch verwerkt zodra de database weer beschikbaar is.\n\n"
                 + "Meest waarschijnlijke oorzaak: Azure SQL Serverless database was gepauzeerd (auto-pause) en kon niet op tijd opstarten.\n"
                 + "De processor probeert 10× met 15 seconden tussentijd (max. 150 seconden). Als de database langer nodig heeft om te starten, verschijnt deze melding.\n\n"
                 + "Controleer in Azure Portal:\n"
                 + "  • Azure SQL Server → Database → Overzicht → Status (moet 'Online' zijn)\n"
                 + "  • Compute + storage → Free monthly vCore amount (maandlimiet bereikt?)\n\n"
                 + "Als de maandlimiet bereikt is: Azure Portal → SQL database → Compute and Storage → \"Continue using database with additional charges\"";

        try
        {
            await graphService.SendReplyAsync(mailbox,
                "URGENT: Database niet bereikbaar — email-processor gepauzeerd", body, null);
            await throttleStore.RegistreerVerstuurdAsync(DatabaseNoodmailSleutel, DateTime.UtcNow);
            // #1143: geen mailbox-/ontvangeradres in logs — SECURITY.md §Laag 5 sluit
            // afzender-/ontvangeradressen expliciet uit, alleen een niet-persoonlijke
            // uitkomst-identifier (DatabaseNoodmailSleutel) blijft over.
            log.LogWarning("Noodmail verstuurd ({Sleutel}) — processor gepauzeerd tot database weer bereikbaar", DatabaseNoodmailSleutel);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon noodmail niet versturen");
        }
    }

    private static bool IsOpenAiQuotaFout(Exception ex)
    {
        var msg = ex.Message + (ex.InnerException?.Message ?? "");
        return msg.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("429", StringComparison.Ordinal);
    }

    // Categoriseert een exception naar een privacy-safe foutomschrijving. (#425)
    // Nooit ruwe ex.Message in noodmails of externe output — kan PII bevatten.
    private static string CategorizeerFout(Exception ex)
    {
        var msg = (ex.Message + (ex.InnerException?.Message ?? "")).ToLowerInvariant();
        if (msg.Contains("insufficient_quota") || msg.Contains("429"))
            return "OpenAI quota overschreden";
        if (msg.Contains("login failed") || msg.Contains("cannot open database") || msg.Contains("connection"))
            return "Database niet beschikbaar";
        if (msg.Contains("404") || msg.Contains("resourcenotfound") || msg.Contains("not found"))
            return "Graph API: bericht niet gevonden";
        if (msg.Contains("401") || msg.Contains("unauthorized") || msg.Contains("403") || msg.Contains("forbidden"))
            return "Graph API: autorisatiefout";
        if (msg.Contains("timeout") || msg.Contains("timed out"))
            return "Time-out bij externe service";
        return "Onverwachte verwerkingsfout";
    }

    // Sanitiseert een foutmelding voor opslag in de DB — verwijdert e-mailadressen en knipt af. (#420)
    private static string SanitizeFoutMelding(string message)
        => EmailSanitizer.SanitizeFoutMelding(message);

    /// <summary>
    /// Bepaalt of de OpenAI-quota-noodmail verstuurd mag worden, op basis van een persistente
    /// registratie in plaats van een static veld (zelfde defect als de database-noodmail, #831).
    /// </summary>
    internal static async Task<bool> MoetOpenAiQuotaNoodmailVersturenAsync(INoodmailThrottleStore throttleStore, DateTime nuUtc)
    {
        var laatsteKeer = await throttleStore.LaatsteKeerVerstuurdAsync(OpenAiQuotaNoodmailSleutel);
        return laatsteKeer is null || (nuUtc - laatsteKeer.Value) >= OpenAiQuotaNoodmailInterval;
    }

    internal static async Task StuurOpenAiNoodmailAsync(
        IEmailGraphService graphService, string foutmelding, INoodmailThrottleStore throttleStore, ILogger log)
    {
        var mailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById(BerichtResponseGenerator.NlTijdzoneId);
        var nlTijd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nlZone);

        var body = $"URGENT: OpenAI quota overschreden — email-processor gepauzeerd.\n\n"
                 + $"Tijdstip: {nlTijd:dd-MM-yyyy HH:mm}\n"
                 + $"Foutmelding: {foutmelding}\n\n"
                 + "De email-processor is gestopt met de huidige batch en stuurt geen herhaalde meldingen binnen 24 uur.\n"
                 + "Onverwerkte emails blijven ongelezen in de inbox en worden opnieuw opgepikt bij de volgende poll.\n\n"
                 + "Acties:\n"
                 + "  • Controleer in Azure Portal → OpenAI resource → Overzicht → Quota\n"
                 + "  • Verhoog de quota-limiet of wacht tot de quota vernieuwt (begin volgende maand)\n"
                 + "  • Als de quota verhoogd is, hervat de processor automatisch bij de volgende poll";

        try
        {
            await graphService.SendReplyAsync(mailbox,
                "URGENT: OpenAI quota overschreden — email-processor gepauzeerd", body, null);
            await throttleStore.RegistreerVerstuurdAsync(OpenAiQuotaNoodmailSleutel, DateTime.UtcNow);
            // #1143: geen mailbox-/ontvangeradres in logs — zie de toelichting bij StuurDatabaseNoodmailAsync.
            log.LogWarning("OpenAI quota-noodmail verstuurd ({Sleutel})", OpenAiQuotaNoodmailSleutel);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon OpenAI quota-noodmail niet versturen");
        }
    }
}
