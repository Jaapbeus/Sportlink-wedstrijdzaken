using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SportlinkFunction.Monitoring;
using SportlinkFunction.TeamResolution;
using Planner.Shared;
using Planner.Shared.Email;

namespace SportlinkFunction.Email;

/// <summary>Gedeelde toestand van één poll van de e-mailverwerker (#1629), zodat de fasemethodes geen tien parameters nodig hebben.</summary>
internal sealed record EmailRunContext(
    ILogger Log,
    IServiceProvider Services,
    ILoggerFactory LoggerFactory,
    IEmailGraphService Graph,
    ITeamResolver TeamResolver,
    IEmailPersistenceService Persistence,
    INoodmailThrottleStore Throttle,
    EmailBatchFilterService BatchFilter,
    EmailReplyPolicyService ReplyPolicy);

/// <summary>Uitkomst van fase 1: wat fase 2 (database) nog moet verwerken.</summary>
internal sealed record LichteFaseResultaat(
    BerichtAiService Ai,
    List<(InkomendBericht Email, BerichtClassificatie Classificatie)> TeVerwerken,
    List<InkomendBericht> MislukteClassificaties)
{
    internal int AantalVoorDatabase => TeVerwerken.Count + MislukteClassificaties.Count;
}

// Fasen van de timer-orkestrator (#1629); de volgorde en de vroege returns staan in Run.
public partial class EmailProcessorFunction
{
    /// <summary>
    /// Fase 1 — licht: Graph API en AI, geen database. Retourneert <c>null</c> als er niets meer te
    /// doen valt (fase 2 overslaan).
    /// </summary>
    private static async Task<LichteFaseResultaat?> VoerFase1LichtAsync(EmailRunContext ctx)
    {
        var log = ctx.Log;

        var teClassificeren = await HaalEnFilterBerichtenAsync(ctx);
        if (teClassificeren is null)
            return null;

        // AI-classificatie voor alle resterende emails — database wordt niet nogmaals gewekt
        var chatClient = ctx.Services.GetService<IChatClient>()
            ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");
        var aiService = new BerichtAiService(ctx.LoggerFactory.CreateLogger<BerichtAiService>(), chatClient);
        var classificationResult = await ClassificeerEnMeldQuotaAsync(ctx, teClassificeren, aiService);
        var classificaties = classificationResult.Classificaties;

        // BuitenScope-emails: alleen Outlook-label, database wordt niet gewekt
        await ctx.BatchFilter.LabelBuitenScopeAsync(classificaties, ctx.Graph, log);

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

        return new LichteFaseResultaat(aiService, teVerwerken, mislukteClassificaties);
    }

    /// <summary>
    /// Haalt de ongelezen berichten op en filtert ze (eigen mailbox, uitsluitingslijst) vóór er één
    /// naar de AI-provider gaat. <c>null</c> = niets te classificeren (reden is gelogd).
    /// </summary>
    private static async Task<List<InkomendBericht>?> HaalEnFilterBerichtenAsync(EmailRunContext ctx)
    {
        var log = ctx.Log;

        var emails = await ctx.Graph.GetUnreadEmailsAsync();
        if (emails.Count == 0)
        {
            log.LogInformation("Geen ongelezen emails");
            return null;
        }

        var eigenMailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";

        // Pre-filter: eigen mailbox en gecachede uitsluitingslijst (geen DB nodig)
        var teClassificeren = await ctx.BatchFilter.PreFilterVoorClassificatieAsync(
            emails,
            eigenMailbox,
            _uitsluitingslijst.Adressen,
            ctx.Graph,
            log);

        if (teClassificeren.Count == 0)
        {
            log.LogInformation("Alle emails gefilterd vóór AI-classificatie");
            return null;
        }

        // Uitsluitingslijst verversen vóór de AI-stap: een adres dat de beheerder net heeft
        // uitgesloten mag niet alsnog naar de externe AI-provider gaan.
        var verseBatch = await FilterMetVerseUitsluitingslijstAsync(
            teClassificeren,
            _uitsluitingslijst,
            async () =>
            {
                await SystemUtilities.WaitForDatabaseAsync(log);
                await SystemUtilities.AppSettings.LoadSettingsAsync(log);
                return await ctx.Persistence.LaadUitgeslotenAdressenAsync(log);
            },
            ctx.BatchFilter,
            DateTime.UtcNow,
            log);

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
    /// AI-classificatie van de batch; bij een OpenAI-quotastoring gaat (hoogstens eens per 24 uur)
    /// een noodmail uit.
    /// </summary>
    private static async Task<EmailClassificationBatchResult> ClassificeerEnMeldQuotaAsync(
        EmailRunContext ctx, List<InkomendBericht> teClassificeren, BerichtAiService aiService)
    {
        var log = ctx.Log;
        var classificationResult = await new EmailClassificationService().ClassificeerBatchAsync(
            teClassificeren,
            email => aiService.ClassificeerBerichtAsync(email.Body, email.Onderwerp, email.Afzender),
            IsOpenAiQuotaFout,
            log);

        if (classificationResult.AiAborted && classificationResult.QuotaException != null)
        {
            var quotaEx = classificationResult.QuotaException;
            if (await MoetOpenAiQuotaNoodmailVersturenAsync(ctx.Throttle, DateTime.UtcNow))
            {
                await StuurOpenAiNoodmailAsync(ctx.Graph, CategorizeerFout(quotaEx), ctx.Throttle, log);
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
    internal static List<InkomendBericht> BepaalMislukteClassificaties(
        List<InkomendBericht> teClassificeren,
        List<(InkomendBericht Email, BerichtClassificatie Classificatie)> classificaties,
        bool aiAborted)
    {
        var geclassificeerdeIds = classificaties
            .Select(c => c.Email.MessageId)
            .ToHashSet(StringComparer.Ordinal);
        return aiAborted
            ? []
            : teClassificeren.Where(e => !geclassificeerdeIds.Contains(e.MessageId)).ToList();
    }

    /// <summary>
    /// Fase 2 — zwaar: database, teamlijst en de verwerkingslus. Alleen aangeroepen als er
    /// non-BuitenScope emails of classificatiefouten zijn.
    /// </summary>
    private static async Task VoerFase2ZwaarAsync(EmailRunContext ctx, LichteFaseResultaat licht)
    {
        var log = ctx.Log;

        if (!await WekDatabaseAsync(ctx, licht))
            return;

        // Onvoorwaardelijk herladen nu de DB wakker is: de hercheck vóór de INSERT hoort op een lijst
        // uit déze invocatie te gebeuren, niet op een kopie die tot de TTL oud kan zijn. (#709)
        await _uitsluitingslijst.HerlaadAsync(
            () => ctx.Persistence.LaadUitgeslotenAdressenAsync(log), DateTime.UtcNow);

        if (!await ControleerTeamlijstGereedAsync(ctx))
            return;

        // Classificatiefouten vastleggen nu de database wakker is — anders bestaat er geen teller en
        // blijven deze berichten oneindig terugkomen (zie BepaalMislukteClassificaties).
        foreach (var mislukt in licht.MislukteClassificaties)
            await RegistreerClassificatieFoutAsync(mislukt, ctx.Graph, ctx.Persistence, log);

        var (verwerkt, fouten) = await VerwerkBatchAsync(ctx, licht);

        log.LogInformation(
            "Email verwerking afgerond: {Verwerkt} verwerkt, {Fouten} fouten, {ClassificatieFouten} classificatiefouten",
            verwerkt, fouten, licht.MislukteClassificaties.Count);
    }

    /// <summary>
    /// Teamlijst-gereedheid hoort HIER, niet in fase 1 (#700). Twee redenen, beide gevonden bij de
    /// adversariële review:
    ///  1. De check heeft de clubCode uit dbo.AppSettings nodig. In fase 1 is die cache op een verse
    ///     worker nog leeg — de check zou dan altijd falen en terugkeren vóór de regel die de settings
    ///     laadt. Dat zette de verwerking permanent stil, met een logregel die naar de verkeerde
    ///     oorzaak wees.
    ///  2. In fase 1 zou hij bij élke poll de database openen, ook bij een lege inbox. Azure SQL
    ///     Serverless pauzeert pas na 60 minuten inactiviteit, dus dat houdt de database 24/7 wakker
    ///     en verbruikt het gratis vCore-budget. Hier is de database toch al wakker.
    /// Retourneert <c>false</c> als de verwerking moet stoppen.
    /// </summary>
    private static async Task<bool> ControleerTeamlijstGereedAsync(EmailRunContext ctx)
    {
        var gereedheid = ctx.Services.GetService<TeamlijstGereedheid>();
        if (gereedheid is not null
            && !await gereedheid.ZorgVoorTeamlijstAsync(SystemUtilities.AppSettings.GetOptionalClubCode()))
        {
            ctx.Log.LogError(
                "Teamherkenning niet mogelijk (geen actieve teams voor deze club) — verwerking overgeslagen "
                + "om verkeerde koppelingen te voorkomen. Controleer of de Sportlink-sync heeft gedraaid.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Wekt de database en laadt de instellingen. Bij een verbindingsfout gaat de (gethrottlede)
    /// noodmail uit en is het resultaat <c>false</c>.
    /// </summary>
    private static async Task<bool> WekDatabaseAsync(EmailRunContext ctx, LichteFaseResultaat licht)
    {
        var log = ctx.Log;
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            await BehandelDatabaseHerstelAsync(ctx.Throttle, log);
            await SystemUtilities.AppSettings.LoadSettingsAsync(log);
            return true;
        }
        catch (Exception dbEx)
        {
            await BehandelDatabaseVerbindingsFoutAsync(
                dbEx, ctx.Graph, licht.AantalVoorDatabase, ctx.Throttle, log);
            return false;
        }
    }

    /// <summary>Verwerkt elk bericht afzonderlijk; een fout bij één bericht stopt de rest niet.</summary>
    private static async Task<(int Verwerkt, int Fouten)> VerwerkBatchAsync(
        EmailRunContext ctx, LichteFaseResultaat licht)
    {
        var log = ctx.Log;
        int verwerkt = 0, fouten = 0;

        foreach (var (email, classificatie) in licht.TeVerwerken)
        {
            try
            {
                await VerwerkEmailAsync(
                    email,
                    classificatie,
                    ctx.Graph,
                    _uitsluitingslijst.Adressen,
                    licht.Ai,
                    ctx.Persistence,
                    ctx.ReplyPolicy,
                    log,
                    ctx.TeamResolver);
                verwerkt++;
            }
            catch (Exception ex)
            {
                fouten++;
                log.LogError(ex, "Fout bij verwerken van email {MessageId} (onderwerp niet gelogd — AVG #210)",
                    email.MessageId);
                await LegVerwerkingsFoutVastAsync(
                    email, SanitizeFoutMelding(ex.Message), ctx.Persistence, log);
            }
        }

        return (verwerkt, fouten);
    }
}
