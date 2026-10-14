using Microsoft.Extensions.Logging;
using Planner.Shared;
using Planner.Shared.Email;

namespace Planner.Endpoints.Email;

/// <summary>Uitkomst van de AI-classificatie van één batch (per tier gebouwd door de eigen classificatieservice).</summary>
public sealed record EmailClassificatieBatch<TBericht>(
    List<(TBericht Email, BerichtClassificatie Classificatie)> Classificaties,
    bool AiAborted,
    Exception? QuotaException);

/// <summary>Uitkomst van fase 1: wat fase 2 (database) nog moet verwerken.</summary>
public sealed record LichteFaseResultaat<TBericht>(
    List<(TBericht Email, BerichtClassificatie Classificatie)> TeVerwerken,
    List<TBericht> MislukteClassificaties)
{
    /// <summary>Aantal berichten dat op de database wacht; staat in de database-noodmail.</summary>
    public int AantalVoorDatabase => TeVerwerken.Count + MislukteClassificaties.Count;
}

/// <summary>
/// De tier-kant van één poll van de e-mailverwerker (#1716 slice 9). De tier levert hier alleen zijn
/// eigen Graph-adapter (<c>EmailGraphService</c>, de enige per tier), databasetoegang, instellingen en de
/// verwerking van één bericht; de volgorde, vroege stops en foutafhandeling staan in
/// <see cref="EmailVerwerkerFasen"/>. Eén instantie per poll: de tier mag er staat in bewaren
/// (bijvoorbeeld de AI-service uit <see cref="BereidAiVoor"/>).
/// </summary>
public interface IEmailVerwerkerPoort<TBericht> where TBericht : IInkomendBericht
{
    /// <summary>De Graph-adapter van de tier (voor de noodmails).</summary>
    IReplyGraph Graph { get; }

    /// <summary>De persistente noodmail-throttle van de tier.</summary>
    INoodmailThrottlePoort Throttle { get; }

    /// <summary>De tier-eigen adviesregels in de noodmails.</summary>
    EmailNoodmailTeksten NoodmailTeksten { get; }

    // ── Fase 1: Graph en AI, geen database ─────────────────────────────────────────────────────

    /// <summary>Haalt de ongelezen berichten op.</summary>
    Task<List<TBericht>> HaalOngelezenAsync();

    /// <summary>Voorfilter zonder database: eigen mailbox, gecachede uitsluitingslijst, automatische afzenders.</summary>
    Task<List<TBericht>> PreFilterAsync(List<TBericht> emails, string eigenMailbox, IReadOnlySet<string> uitgeslotenCache);

    /// <summary>Filtert de batch op een (vers geladen) uitsluitingslijst.</summary>
    List<TBericht> FilterUitgesloten(List<TBericht> emails, IReadOnlySet<string> uitgeslotenAdressen);

    /// <summary>Wekt de database, laadt de instellingen en de uitsluitingslijst (vóór de AI-stap).</summary>
    Task<HashSet<string>> WekEnLaadUitsluitingslijstAsync();

    /// <summary>
    /// Zet de AI-classificatie klaar. <c>false</c> = niet beschikbaar en de poll stopt; de tier logt de
    /// reden zelf (of gooit, zoals de SQL Server-tier).
    /// </summary>
    bool BereidAiVoor();

    /// <summary>AI-classificatie van de batch met de eigen classificatieservice van de tier.</summary>
    Task<EmailClassificatieBatch<TBericht>> ClassificeerAsync(List<TBericht> teClassificeren);

    /// <summary>Labelt de buiten-scope-berichten in Outlook en markeert ze als gelezen; database slaapt.</summary>
    Task LabelBuitenScopeAsync(List<(TBericht Email, BerichtClassificatie Classificatie)> classificaties);

    // ── Fase 2: database ───────────────────────────────────────────────────────────────────────

    /// <summary>Wacht tot de database bereikbaar is; gooit bij een verbindingsfout.</summary>
    Task WachtOpDatabaseAsync();

    /// <summary>Laadt de instellingen; gooit bij een verbindingsfout.</summary>
    Task LaadInstellingenAsync();

    /// <summary>Laadt de uitsluitingslijst uit de (wakkere) database.</summary>
    Task<HashSet<string>> LaadUitsluitingslijstAsync();

    /// <summary>Tier-eigen voorbereiding van de teamherkenning. <c>false</c> = verwerking overslaan (reden gelogd).</summary>
    Task<bool> BereidVerwerkingVoorAsync();

    /// <summary>Legt een mislukte AI-classificatie vast (pogingenteller).</summary>
    Task RegistreerClassificatieFoutAsync(TBericht email);

    /// <summary>Verwerkt één geclassificeerd bericht.</summary>
    Task VerwerkEmailAsync(TBericht email, BerichtClassificatie classificatie, IReadOnlySet<string> uitgeslotenAdressen);

    /// <summary>Legt een verwerkingsfout vast; de melding is al gesaneerd.</summary>
    Task LegVerwerkingsFoutVastAsync(TBericht email, string foutMelding);
}

/// <summary>
/// Fase-orkestratie van de e-mailverwerker (timer <c>ProcessIncomingEmails</c>), tier-onafhankelijk
/// sinds #1716 slice 9 (samen met #1271). Fase 1 is licht (Graph en AI, de database slaapt); fase 2
/// wekt de database alleen als er iets te verwerken is. De tier levert alles wat database, Graph of
/// instellingen raakt via <see cref="IEmailVerwerkerPoort{TBericht}"/>; hier staat geen SQL en geen
/// Graph SDK, en <c>EmailGraphService</c> blijft de enige Graph-adapter per tier.
/// </summary>
public static class EmailVerwerkerFasen
{
    /// <summary>Eén poll: fase 1, en alleen als die iets oplevert fase 2.</summary>
    public static async Task VoerUitAsync<TBericht>(
        IEmailVerwerkerPoort<TBericht> poort, UitsluitingslijstCache uitsluitingslijst, ILogger log)
        where TBericht : IInkomendBericht
    {
        var licht = await VoerFase1LichtAsync(poort, uitsluitingslijst, log);
        if (licht is null)
            return;

        await VoerFase2ZwaarAsync(poort, uitsluitingslijst, licht, log);
    }

    /// <summary>
    /// Fase 1 — licht: Graph API en AI, geen database. Retourneert <c>null</c> als er niets meer te
    /// doen valt (fase 2 overslaan).
    /// </summary>
    internal static async Task<LichteFaseResultaat<TBericht>?> VoerFase1LichtAsync<TBericht>(
        IEmailVerwerkerPoort<TBericht> poort, UitsluitingslijstCache uitsluitingslijst, ILogger log)
        where TBericht : IInkomendBericht
    {
        var teClassificeren = await HaalEnFilterBerichtenAsync(poort, uitsluitingslijst, log);
        if (teClassificeren is null)
            return null;

        if (!poort.BereidAiVoor())
            return null;

        var classificationResult = await ClassificeerEnMeldQuotaAsync(poort, teClassificeren, log);
        var classificaties = classificationResult.Classificaties;

        // BuitenScope-emails: alleen Outlook-label, database wordt niet gewekt
        await poort.LabelBuitenScopeAsync(classificaties);

        var teVerwerken = classificaties
            .Where(c => c.Classificatie.Type != VerzoekType.BuitenScope)
            .ToList();
        var mislukteClassificaties = BepaalMislukteClassificaties(
            teClassificeren, classificaties, classificationResult.AiAborted);

        if (teVerwerken.Count == 0 && mislukteClassificaties.Count == 0)
        {
            var aantalBuitenScope = classificaties.Count(c => c.Classificatie.Type == VerzoekType.BuitenScope);
            log.LogInformation(
                "Alle {Aantal} emails buiten scope{Afgebroken} — geen verwerking in de database nodig",
                aantalBuitenScope,
                classificationResult.AiAborted ? " (AI batch vroegtijdig gestopt)" : "");
            return null; // Fase 2 overslaan
        }

        return new LichteFaseResultaat<TBericht>(teVerwerken, mislukteClassificaties);
    }

    /// <summary>
    /// Haalt de ongelezen berichten op en filtert ze (eigen mailbox, uitsluitingslijst) vóór er één
    /// naar de AI-provider gaat. <c>null</c> = niets te classificeren (reden is gelogd).
    /// </summary>
    private static async Task<List<TBericht>?> HaalEnFilterBerichtenAsync<TBericht>(
        IEmailVerwerkerPoort<TBericht> poort, UitsluitingslijstCache uitsluitingslijst, ILogger log)
        where TBericht : IInkomendBericht
    {
        var emails = await poort.HaalOngelezenAsync();
        if (emails.Count == 0)
        {
            log.LogInformation("Geen ongelezen emails");
            return null;
        }

        var eigenMailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";

        // Pre-filter: eigen mailbox en gecachede uitsluitingslijst (geen DB nodig)
        var teClassificeren = await poort.PreFilterAsync(emails, eigenMailbox, uitsluitingslijst.Adressen);
        if (teClassificeren.Count == 0)
        {
            log.LogInformation("Alle emails gefilterd vóór AI-classificatie");
            return null;
        }

        // Uitsluitingslijst verversen vóór de AI-stap: een adres dat de beheerder net heeft
        // uitgesloten mag niet alsnog naar de externe AI-provider gaan.
        var verseBatch = await FilterMetVerseUitsluitingslijstAsync(
            teClassificeren, uitsluitingslijst, poort.WekEnLaadUitsluitingslijstAsync,
            poort.FilterUitgesloten, DateTime.UtcNow, log);

        if (verseBatch is null)
            return null;

        if (verseBatch.Count == 0)
        {
            log.LogInformation("Alle emails gefilterd na uitsluitingslijst-check");
            return null;
        }

        return verseBatch;
    }

    /// <summary>
    /// Zorgt dat de uitsluitingslijst niet verouderd is en filtert de batch ermee — vóór er één
    /// bericht naar de AI-provider gaat (#709). Retourneert <c>null</c> als er niet geclassificeerd
    /// mag worden omdat de lijst nooit geladen is en ook nu niet te laden is (fail-closed, #423).
    /// </summary>
    public static async Task<List<TBericht>?> FilterMetVerseUitsluitingslijstAsync<TBericht>(
        List<TBericht> teClassificeren,
        UitsluitingslijstCache uitsluitingslijst,
        Func<Task<HashSet<string>>> laadLijstAsync,
        Func<List<TBericht>, IReadOnlySet<string>, List<TBericht>> filterUitgesloten,
        DateTime nuUtc,
        ILogger log)
    {
        var stand = await uitsluitingslijst.VerversIndienVerouderdAsync(laadLijstAsync, nuUtc, log);

        return stand switch
        {
            UitsluitingslijstStand.Ontbreekt => null,
            // Alleen na een verse lijst kan de uitkomst van het voorfilter achterhaald zijn.
            UitsluitingslijstStand.Ververst => filterUitgesloten(teClassificeren, uitsluitingslijst.Adressen),
            _ => teClassificeren
        };
    }

    /// <summary>
    /// AI-classificatie van de batch; bij een OpenAI-quotastoring gaat (hoogstens eens per 24 uur)
    /// een noodmail uit.
    /// </summary>
    private static async Task<EmailClassificatieBatch<TBericht>> ClassificeerEnMeldQuotaAsync<TBericht>(
        IEmailVerwerkerPoort<TBericht> poort, List<TBericht> teClassificeren, ILogger log)
        where TBericht : IInkomendBericht
    {
        var classificationResult = await poort.ClassificeerAsync(teClassificeren);

        if (classificationResult.AiAborted && classificationResult.QuotaException != null)
        {
            var quotaEx = classificationResult.QuotaException;
            if (await EmailNoodmailMelding.MoetOpenAiQuotaNoodmailVersturenAsync(poort.Throttle, DateTime.UtcNow))
            {
                await EmailNoodmailMelding.StuurOpenAiNoodmailAsync(
                    poort.Graph, EmailFoutCategorie.Categoriseer(quotaEx), poort.Throttle, poort.NoodmailTeksten, log);
            }
            else
            {
                log.LogWarning("OpenAI quota-noodmail al verstuurd binnen 24u — geen herhaling");
            }
        }

        return classificationResult;
    }

    /// <summary>
    /// Berichten waarvoor de AI-classificatie faalde zitten niet in classificaties en komen dus
    /// ook niet in fase 2. Ze blijven ongelezen en komen elke poll terug. Ze worden in fase 2
    /// alsnog geregistreerd zodat de pogingenteller werkt. Bij een afgebroken batch
    /// (OpenAI-quota) is voor de resterende berichten géén poging gedaan; die mogen niet
    /// meetellen, anders straft een quota-storing onschuldige berichten.
    /// </summary>
    public static List<TBericht> BepaalMislukteClassificaties<TBericht>(
        List<TBericht> teClassificeren,
        List<(TBericht Email, BerichtClassificatie Classificatie)> classificaties,
        bool aiAborted)
        where TBericht : IInkomendBericht
    {
        var geclassificeerdeIds = classificaties
            .Select(c => c.Email.MessageId)
            .ToHashSet(StringComparer.Ordinal);
        return aiAborted
            ? []
            : teClassificeren.Where(e => !geclassificeerdeIds.Contains(e.MessageId)).ToList();
    }

    /// <summary>
    /// Fase 2 — zwaar: database, teamherkenning en de verwerkingslus. Alleen aangeroepen als er
    /// non-BuitenScope emails of classificatiefouten zijn.
    /// </summary>
    internal static async Task VoerFase2ZwaarAsync<TBericht>(
        IEmailVerwerkerPoort<TBericht> poort, UitsluitingslijstCache uitsluitingslijst,
        LichteFaseResultaat<TBericht> licht, ILogger log)
        where TBericht : IInkomendBericht
    {
        if (!await WekDatabaseAsync(poort, licht, log))
            return;

        // Onvoorwaardelijk herladen nu de DB wakker is: de hercheck vóór de INSERT hoort op een lijst
        // uit déze invocatie te gebeuren, niet op een kopie die tot de TTL oud kan zijn. (#709)
        await uitsluitingslijst.HerlaadAsync(poort.LaadUitsluitingslijstAsync, DateTime.UtcNow);

        // Teamherkenning voorbereiden hoort hier, niet in fase 1: de database is nu toch wakker (#700).
        if (!await poort.BereidVerwerkingVoorAsync())
            return;

        // Classificatiefouten vastleggen nu de database wakker is — anders bestaat er geen teller en
        // blijven deze berichten oneindig terugkomen (zie BepaalMislukteClassificaties).
        foreach (var mislukt in licht.MislukteClassificaties)
            await poort.RegistreerClassificatieFoutAsync(mislukt);

        var (verwerkt, fouten) = await VerwerkBatchAsync(poort, uitsluitingslijst, licht, log);

        log.LogInformation(
            "Email verwerking afgerond: {Verwerkt} verwerkt, {Fouten} fouten, {ClassificatieFouten} classificatiefouten",
            verwerkt, fouten, licht.MislukteClassificaties.Count);
    }

    /// <summary>
    /// Wekt de database en laadt de instellingen. Bij een verbindingsfout gaat de (gethrottlede)
    /// noodmail uit en is het resultaat <c>false</c>.
    /// </summary>
    private static async Task<bool> WekDatabaseAsync<TBericht>(
        IEmailVerwerkerPoort<TBericht> poort, LichteFaseResultaat<TBericht> licht, ILogger log)
        where TBericht : IInkomendBericht
    {
        try
        {
            await poort.WachtOpDatabaseAsync();
            await EmailNoodmailMelding.BehandelDatabaseHerstelAsync(poort.Throttle, log);
            await poort.LaadInstellingenAsync();
            return true;
        }
        catch (Exception dbEx)
        {
            await EmailNoodmailMelding.BehandelDatabaseVerbindingsFoutAsync(
                dbEx, poort.Graph, licht.AantalVoorDatabase, poort.Throttle, poort.NoodmailTeksten, log);
            return false;
        }
    }

    /// <summary>Verwerkt elk bericht afzonderlijk; een fout bij één bericht stopt de rest niet.</summary>
    private static async Task<(int Verwerkt, int Fouten)> VerwerkBatchAsync<TBericht>(
        IEmailVerwerkerPoort<TBericht> poort, UitsluitingslijstCache uitsluitingslijst,
        LichteFaseResultaat<TBericht> licht, ILogger log)
        where TBericht : IInkomendBericht
    {
        int verwerkt = 0, fouten = 0;

        foreach (var (email, classificatie) in licht.TeVerwerken)
        {
            try
            {
                await poort.VerwerkEmailAsync(email, classificatie, uitsluitingslijst.Adressen);
                verwerkt++;
            }
            catch (Exception ex)
            {
                fouten++;
                log.LogError(ex, "Fout bij verwerken van email {MessageId} (onderwerp niet gelogd — AVG #210)",
                    email.MessageId);
                await poort.LegVerwerkingsFoutVastAsync(email, EmailSanitizer.SanitizeFoutMelding(ex.Message));
            }
        }

        return (verwerkt, fouten);
    }
}
