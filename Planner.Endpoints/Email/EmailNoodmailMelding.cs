using Microsoft.Extensions.Logging;
using Planner.Shared.Email;
using Planner.Shared.Monitoring;

namespace Planner.Endpoints.Email;

/// <summary>
/// Persistente throttle-opslag voor de noodmails van de e-mailverwerker (#831): "wanneer is deze
/// noodmail voor het laatst verstuurd". Het per tier eigen <c>INoodmailThrottleStore</c> erft deze
/// poort, zodat de DI-registratie per tier ongewijzigd blijft (#1716 slice 9).
/// </summary>
public interface INoodmailThrottlePoort
{
    /// <summary>Tijdstip (UTC) van de laatste verzending voor deze sleutel, of <c>null</c> als er niets openstaat.</summary>
    Task<DateTime?> LaatsteKeerVerstuurdAsync(string sleutel);

    /// <summary>Legt vast dat de noodmail voor deze sleutel is verstuurd.</summary>
    Task RegistreerVerstuurdAsync(string sleutel, DateTime verstuurdOpUtc);

    /// <summary>Wist de registratie, zodat een volgende uitval weer een verse melding geeft.</summary>
    Task WisAsync(string sleutel);
}

/// <summary>
/// De tier-eigen adviesregels in de noodmails: waar de beheerder moet kijken verschilt per databasetier
/// en hosting. De rest van de tekst is gelijk en staat in <see cref="EmailNoodmailMelding"/>.
/// </summary>
/// <param name="DatabaseAdvies">Slot van de database-noodmail, na de vaste uitleg.</param>
/// <param name="OpenAiActies">Eerste actieregels van de OpenAI-quota-noodmail (elk eindigend op een regelovergang).</param>
public sealed record EmailNoodmailTeksten(string DatabaseAdvies, string OpenAiActies);

/// <summary>
/// Noodmails van de e-mailverwerker (#831, #1143), tier-onafhankelijk sinds #1716 slice 9: de
/// throttle-besluiten (één database-noodmail per uitval, één OpenAI-quota-noodmail per 24 uur) en de
/// verzending via de enige Graph-adapter van de tier (<see cref="IReplyGraph"/>). De registratie staat
/// in <see cref="INoodmailThrottlePoort"/>, niet in een static veld, zodat het gedrag niet afhangt van
/// cold starts.
/// </summary>
public static class EmailNoodmailMelding
{
    /// <summary>Gedeelde sleutel met de database-uitvalmonitor van beide tiers (<see cref="DatabaseUitvalCore.NoodmailSleutel"/>).</summary>
    public const string DatabaseNoodmailSleutel = DatabaseUitvalCore.NoodmailSleutel;

    /// <summary>Sleutel van de OpenAI-quota-noodmail.</summary>
    public const string OpenAiQuotaNoodmailSleutel = "openai-quota-noodmail";

    /// <summary>Minimale tijd tussen twee OpenAI-quota-noodmails.</summary>
    public static readonly TimeSpan OpenAiQuotaNoodmailInterval = TimeSpan.FromHours(24);

    private const string NlTijdzoneId = "W. Europe Standard Time";

    /// <summary>
    /// Reageert op een geslaagde databaseverbinding: als er een noodmail-registratie openstaat van een
    /// eerdere uitval, wordt die gewist zodat een volgende, nieuwe uitval weer een verse melding oplevert.
    /// </summary>
    public static async Task BehandelDatabaseHerstelAsync(INoodmailThrottlePoort throttle, ILogger log)
    {
        if (await throttle.LaatsteKeerVerstuurdAsync(DatabaseNoodmailSleutel) is not null)
        {
            await throttle.WisAsync(DatabaseNoodmailSleutel);
            log.LogInformation("Database weer bereikbaar — email processor hervat");
        }
    }

    /// <summary>
    /// Reageert op een mislukte databaseverbinding: stuurt een noodmail, tenzij er al één openstaat voor
    /// deze uitval.
    /// </summary>
    public static async Task BehandelDatabaseVerbindingsFoutAsync(
        Exception dbEx, IReplyGraph graph, int aantalOnverwerkt, INoodmailThrottlePoort throttle,
        EmailNoodmailTeksten teksten, ILogger log)
    {
        if (await throttle.LaatsteKeerVerstuurdAsync(DatabaseNoodmailSleutel) is null)
        {
            log.LogError(dbEx, "Database niet beschikbaar — stuur noodmail");
            await StuurDatabaseNoodmailAsync(
                graph, aantalOnverwerkt, EmailFoutCategorie.Categoriseer(dbEx), throttle, teksten, log);
        }
        else
        {
            log.LogWarning("Email processor gepauzeerd — database nog niet bereikbaar (noodmail al verstuurd)");
        }
    }

    /// <summary>
    /// Stuurt een noodmail als de database niet beschikbaar is. Emails blijven ongelezen in de inbox en
    /// worden bij de volgende poll opnieuw opgepikt. Een mislukte verzending wordt niet geregistreerd.
    /// </summary>
    public static async Task StuurDatabaseNoodmailAsync(
        IReplyGraph graph, int aantalEmails, string foutmelding, INoodmailThrottlePoort throttle,
        EmailNoodmailTeksten teksten, ILogger log)
    {
        var body = "URGENT: De database is niet bereikbaar.\n\n"
                 + $"Tijdstip: {NlTijd():dd-MM-yyyy HH:mm}\n"
                 + $"Foutmelding: {foutmelding}\n"
                 + $"Onverwerkte emails: {aantalEmails}\n\n"
                 + "De email-processor is automatisch GEPAUZEERD. Er worden geen herhaalde meldingen verstuurd.\n"
                 + "De processor hervat automatisch zodra de database weer bereikbaar is.\n\n"
                 + "De emails blijven ongelezen in de inbox en worden automatisch verwerkt zodra de database weer beschikbaar is.\n\n"
                 + teksten.DatabaseAdvies;

        try
        {
            await graph.SendReplyAsync(Mailbox(),
                "URGENT: Database niet bereikbaar — email-processor gepauzeerd", body, null);
            await throttle.RegistreerVerstuurdAsync(DatabaseNoodmailSleutel, DateTime.UtcNow);
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

    /// <summary>
    /// Bepaalt of de OpenAI-quota-noodmail verstuurd mag worden, op basis van de persistente registratie
    /// (hoogstens eens per <see cref="OpenAiQuotaNoodmailInterval"/>).
    /// </summary>
    public static async Task<bool> MoetOpenAiQuotaNoodmailVersturenAsync(INoodmailThrottlePoort throttle, DateTime nuUtc)
    {
        var laatsteKeer = await throttle.LaatsteKeerVerstuurdAsync(OpenAiQuotaNoodmailSleutel);
        return laatsteKeer is null || (nuUtc - laatsteKeer.Value) >= OpenAiQuotaNoodmailInterval;
    }

    /// <summary>Stuurt de OpenAI-quota-noodmail; een mislukte verzending wordt niet geregistreerd.</summary>
    public static async Task StuurOpenAiNoodmailAsync(
        IReplyGraph graph, string foutmelding, INoodmailThrottlePoort throttle, EmailNoodmailTeksten teksten, ILogger log)
    {
        var body = "URGENT: OpenAI quota overschreden — email-processor gepauzeerd.\n\n"
                 + $"Tijdstip: {NlTijd():dd-MM-yyyy HH:mm}\n"
                 + $"Foutmelding: {foutmelding}\n\n"
                 + "De email-processor is gestopt met de huidige batch en stuurt geen herhaalde meldingen binnen 24 uur.\n"
                 + "Onverwerkte emails blijven ongelezen in de inbox en worden opnieuw opgepikt bij de volgende poll.\n\n"
                 + "Acties:\n"
                 + teksten.OpenAiActies
                 + "  • Als de quota verhoogd is, hervat de processor automatisch bij de volgende poll";

        try
        {
            await graph.SendReplyAsync(Mailbox(),
                "URGENT: OpenAI quota overschreden — email-processor gepauzeerd", body, null);
            await throttle.RegistreerVerstuurdAsync(OpenAiQuotaNoodmailSleutel, DateTime.UtcNow);
            // #1143: geen mailbox-/ontvangeradres in logs — zie de toelichting bij StuurDatabaseNoodmailAsync.
            log.LogWarning("OpenAI quota-noodmail verstuurd ({Sleutel})", OpenAiQuotaNoodmailSleutel);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon OpenAI quota-noodmail niet versturen");
        }
    }

    // De noodmail gaat naar de eigen mailbox van de coördinator (app-instelling, per deployment).
    private static string Mailbox() => Environment.GetEnvironmentVariable("GraphMailbox") ?? "";

    private static DateTime NlTijd()
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(NlTijdzoneId));
}
