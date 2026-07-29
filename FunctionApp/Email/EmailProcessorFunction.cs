using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Newtonsoft.Json;
using SportlinkFunction.Processing;
using SportlinkFunction.TeamResolution;

namespace SportlinkFunction.Email;

/// <summary>Wat er met een inkomend bericht moet gebeuren op basis van een eerdere verwerking.</summary>
internal enum VerwerkingsBesluit
{
    /// <summary>Nog niet eerder gezien — nieuwe verwerkingsrij aanmaken.</summary>
    NieuweVerwerking,

    /// <summary>Eerdere poging is niet afgerond — bestaande rij hergebruiken en opnieuw verwerken.</summary>
    HerhaalVerwerking,

    /// <summary>Definitief afgehandeld — niets meer doen (en dus zeker niet opnieuw antwoorden).</summary>
    OverslaanAlAfgerond,

    /// <summary>Te vaak mislukt — opgeven zodat het bericht de wachtrij niet blijft blokkeren.</summary>
    OpgevenNaMaxPogingen,

    /// <summary>
    /// Er is een verzendpoging vastgelegd waarvan de uitkomst onbekend is (#716). Niet opnieuw
    /// versturen — het eerste antwoord kan de deur al uit zijn — maar ter beoordeling neerleggen.
    /// </summary>
    OnbeslistNaVerzendPoging
}

/// <summary>
/// Idempotentiebesluit voor de e-mailverwerking (#712).
///
/// <para>
/// Het bestaan van een rij in <c>planner.EmailVerwerking</c> betekende voorheen "al verwerkt".
/// Dat is fout: de rij wordt aangemaakt vóór de verwerking, dus élke fout daarna (verzendfout,
/// plannerfout, templatefout) liet een rij achter waardoor de volgende poll het bericht oversloeg
/// én als gelezen markeerde. Netto kreeg de afzender nooit antwoord en verdween het bericht uit de
/// wachtrij. Het besluit hangt daarom af van de <b>eindstatus</b>, niet van het bestaan van de rij.
/// </para>
///
/// <para>
/// Puur en zonder afhankelijkheden, zodat elk faalscenario los te testen is.
/// </para>
/// </summary>
internal static class EmailIdempotentie
{
    /// <summary>
    /// Maximaal aantal verwerkingspogingen per bericht. Drie is genoeg om tijdelijke fouten (Graph
    /// 429/503, een net gepauzeerde database, een time-out) uit te zitten, en laag genoeg dat een
    /// structurele fout de wachtrij niet lang bezet houdt: de poll pakt de 10 oudste ongelezen
    /// berichten, dus tien blijvend falende berichten zouden anders alle nieuwe post tegenhouden.
    /// </summary>
    internal const int MaxPogingen = 3;

    /// <summary>Statussen waarna een bericht niet opnieuw verwerkt mag worden.</summary>
    private static readonly EmailStatus[] DefinitieveStatussen =
    [
        EmailStatus.AntwoordVerstuurd,
        EmailStatus.GeenAntwoordNodig,
        EmailStatus.BuitenScope
    ];

    /// <summary>
    /// Is deze verwerking definitief afgerond? <c>AntwoordVerstuurd</c> is hier leidend: dat wordt
    /// uitsluitend vastgelegd nádat een antwoord echt verstuurd is, dus een gezette waarde sluit een
    /// tweede antwoord uit — ook als de status daarna nog op 'Fout' is gezet of onbekend is.
    /// </summary>
    internal static bool IsDefinitief(EmailVerwerkingStand stand)
        => stand.AntwoordVerstuurd
           || (Enum.TryParse<EmailStatus>(stand.Status, out var status)
               && DefinitieveStatussen.Contains(status));

    internal static VerwerkingsBesluit Bepaal(EmailVerwerkingStand? stand)
    {
        if (stand is null)
            return VerwerkingsBesluit.NieuweVerwerking;

        if (IsDefinitief(stand))
            return VerwerkingsBesluit.OverslaanAlAfgerond;

        // Vóór de pogingengrens: een onbekende verzenduitkomst is geen "mislukte poging" die je nog een
        // keer mag proberen. Zou dit ná de grens staan, dan zou een bericht met twee eerdere pogingen
        // alsnog opnieuw verstuurd worden — precies wat #716 moet voorkomen.
        if (stand.VerzendPogingOnbeslist)
            return VerwerkingsBesluit.OnbeslistNaVerzendPoging;

        return stand.Pogingen >= MaxPogingen
            ? VerwerkingsBesluit.OpgevenNaMaxPogingen
            : VerwerkingsBesluit.HerhaalVerwerking;
    }
}

/// <summary>Uitkomst van een poging om de uitsluitingslijst te verversen (#709).</summary>
internal enum UitsluitingslijstStand
{
    /// <summary>Binnen de geldigheidsduur — er is niets uit de database gelezen.</summary>
    Actueel,

    /// <summary>Opnieuw uit de database gelezen; de lijst kan gewijzigd zijn.</summary>
    Ververst,

    /// <summary>Herladen mislukt, maar er is een eerdere lijst — die blijft gelden.</summary>
    VerouderdBehouden,

    /// <summary>Nooit geladen én nu niet te laden — er mag niet geclassificeerd worden.</summary>
    Ontbreekt
}

/// <summary>
/// In-memory kopie van de uitsluitingslijst met een geldigheidsduur (#709).
///
/// <para>
/// De lijst werd alleen bij een cold start en in fase 2 geladen. Fase 2 wordt niet bereikt zolang
/// elk bericht in de batch buiten scope valt, dus bleef de kopie in fase 1 verouderd: een adres dat
/// de beheerder net had uitgesloten kreeg terecht géén antwoord (de hercheck vóór de INSERT werkt
/// wel), maar de inhoud van het bericht was dan al naar de externe AI-provider gestuurd. Met een
/// geldigheidsduur wordt de lijst vóór de AI-stap vernieuwd, zonder bij elke poll de database te
/// wekken.
/// </para>
/// </summary>
internal sealed class UitsluitingslijstCache
{
    /// <summary>
    /// Geldigheidsduur van de kopie. Bewust ruimer dan het poll-interval van 5 minuten: bij élke poll
    /// herladen zou de Azure SQL Serverless-database wakker houden voor batches die anders helemaal
    /// niet in de database terechtkomen. Vijftien minuten begrenst hoe lang een net uitgesloten adres
    /// nog een AI-call kan kosten, zonder die database-kosten.
    /// </summary>
    internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    // volatile / Volatile.Read: meerdere invocaties lezen dezelfde statische instantie (#382).
    private volatile HashSet<string> _adressen = new(StringComparer.OrdinalIgnoreCase);
    private long _geladenOpTicksUtc;

    internal IReadOnlySet<string> Adressen => _adressen;

    /// <summary>Is de lijst ooit met succes geladen? Zo niet, dan geldt fail-closed. (#423)</summary>
    internal bool IsGeladen => Volatile.Read(ref _geladenOpTicksUtc) != 0;

    internal bool IsVerouderd(DateTime nuUtc)
    {
        var ticks = Volatile.Read(ref _geladenOpTicksUtc);
        return ticks == 0 || nuUtc - new DateTime(ticks, DateTimeKind.Utc) >= Ttl;
    }

    internal async Task<UitsluitingslijstStand> VerversIndienVerouderdAsync(
        Func<Task<HashSet<string>>> laadAsync, DateTime nuUtc, ILogger log)
    {
        if (!IsVerouderd(nuUtc))
            return UitsluitingslijstStand.Actueel;

        try
        {
            await HerlaadAsync(laadAsync, nuUtc);
            return UitsluitingslijstStand.Ververst;
        }
        catch (Exception ex)
        {
            if (!IsGeladen)
            {
                log.LogError(ex, "Uitsluitingslijst niet beschikbaar — AI-verwerking uitgesteld (fail-closed)");
                return UitsluitingslijstStand.Ontbreekt;
            }

            // Wél een eerdere lijst: doorgaan met die lijst is veiliger dan de verwerking stilzetten,
            // en het is precies het gedrag van vóór deze TTL.
            log.LogWarning(ex,
                "Uitsluitingslijst kon niet worden ververst — eerdere lijst met {Aantal} adressen blijft gelden",
                _adressen.Count);
            return UitsluitingslijstStand.VerouderdBehouden;
        }
    }

    /// <summary>
    /// Laadt de lijst onvoorwaardelijk opnieuw. Gebruikt door fase 2, waar de hercheck vóór de INSERT
    /// op een lijst uit déze invocatie moet gebeuren en niet op een kopie die tot de TTL oud kan zijn.
    /// </summary>
    internal async Task HerlaadAsync(Func<Task<HashSet<string>>> laadAsync, DateTime nuUtc)
    {
        _adressen = await laadAsync();
        Volatile.Write(ref _geladenOpTicksUtc, nuUtc.Ticks);
    }
}

public class EmailProcessorFunction
{
    private const string GeenAiAntwoordLabel = "Geen AI antwoord";

    // volatile voor thread-safe reads vanuit meerdere invocaties (#382)
    private static volatile bool _databaseNoodmailVerstuurd;
    private static DateTime? _openAiQuotaNoodmailVerstuurdenOp;
    // Uitsluitingslijst: geladen vóór elke AI-classificatie (fail-closed bij cold start). (#423, #709)
    private static readonly UitsluitingslijstCache _uitsluitingslijst = new();

    [Function("ProcessIncomingEmails")]
    public async Task Run(
        [TimerTrigger("%EMAIL_POLL_SCHEDULE%")] TimerInfo timer,
        FunctionContext context)
    {
        var log = context.GetLogger("ProcessIncomingEmails");

        if (!string.Equals(Environment.GetEnvironmentVariable("EmailProcessorEnabled"),
                "true", StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("Email processor uitgeschakeld");
            return;
        }

        var graphClient = context.InstanceServices.GetService<GraphServiceClient>();
        if (graphClient == null)
        {
            log.LogError("GraphServiceClient niet beschikbaar — controleer Graph settings");
            return;
        }

        var loggerFactory = context.InstanceServices.GetRequiredService<ILoggerFactory>();

        // Teamnaam→TeamId-vertaallaag (#692). Sinds #700 is dit het ENIGE pad waarlangs een team wordt
        // herkend — de oude regex-normalisatie bestaat niet meer. Ontbreekt de resolver, dan zou elke
        // mail zonder teamherkenning verwerkt worden; dat is erger dan niet verwerken, dus we stoppen.
        var teamResolver = context.InstanceServices.GetService<ITeamResolver>();
        if (teamResolver is null)
        {
            log.LogError("Teamresolutie niet beschikbaar (ITeamResolver niet geregistreerd) — verwerking overgeslagen");
            return;
        }

        IEmailGraphService graphService = new EmailGraphService(graphClient, loggerFactory.CreateLogger<EmailGraphService>());
        IEmailPersistenceService persistenceService = new EmailPersistenceService();
        var batchFilterService = new EmailBatchFilterService();
        var classificationService = new EmailClassificationService();
        var replyPolicyService = new EmailReplyPolicyService();

        // ── FASE 1: licht — Graph API en AI, geen database ──────────────────────────

        var emails = await graphService.GetUnreadEmailsAsync();
        if (emails.Count == 0)
        {
            log.LogInformation("Geen ongelezen emails");
            return;
        }

        var eigenMailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";

        // Pre-filter: eigen mailbox en gecachede uitsluitingslijst (geen DB nodig)
        var teClassificeren = await batchFilterService.PreFilterVoorClassificatieAsync(
            emails,
            eigenMailbox,
            _uitsluitingslijst.Adressen,
            graphService,
            log);

        if (teClassificeren.Count == 0)
        {
            log.LogInformation("Alle emails gefilterd vóór AI-classificatie");
            return;
        }

        // Uitsluitingslijst verversen vóór de AI-stap: een adres dat de beheerder net heeft
        // uitgesloten mag niet alsnog naar de externe AI-provider gaan. (#423, #709)
        var verseBatch = await FilterMetVerseUitsluitingslijstAsync(
            teClassificeren,
            _uitsluitingslijst,
            async () =>
            {
                await SystemUtilities.WaitForDatabaseAsync(log);
                await SystemUtilities.AppSettings.LoadSettingsAsync(log);
                return await persistenceService.LaadUitgeslotenAdressenAsync(log);
            },
            batchFilterService,
            DateTime.UtcNow,
            log);

        if (verseBatch is null)
            return;

        teClassificeren = verseBatch;

        if (teClassificeren.Count == 0)
        {
            log.LogInformation("Alle emails gefilterd na uitsluitingslijst-check");
            return;
        }

        // AI-classificatie voor alle resterende emails — database wordt niet nogmaals gewekt
        var chatClient = context.InstanceServices.GetService<Microsoft.Extensions.AI.IChatClient>()
            ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");
        var aiService = new BerichtAiService(loggerFactory.CreateLogger<BerichtAiService>(), chatClient);
        var classificationResult = await classificationService.ClassificeerBatchAsync(
            teClassificeren,
            email => aiService.ClassificeerBerichtAsync(email.Body, email.Onderwerp, email.Afzender),
            IsOpenAiQuotaFout,
            log);

        if (classificationResult.AiAborted && classificationResult.QuotaException != null)
        {
            var quotaEx = classificationResult.QuotaException;
            if (_openAiQuotaNoodmailVerstuurdenOp == null
                || (DateTime.UtcNow - _openAiQuotaNoodmailVerstuurdenOp.Value).TotalHours >= 24)
            {
                await StuurOpenAiNoodmailAsync(graphService, CategorizeerFout(quotaEx), log);
            }
            else
            {
                log.LogWarning("OpenAI quota-noodmail al verstuurd binnen 24u — geen herhaling");
            }
        }

        var classificaties = classificationResult.Classificaties;

        // BuitenScope-emails: alleen Outlook-label, database wordt niet gewekt
        await batchFilterService.LabelBuitenScopeAsync(classificaties, graphService, log);

        var teVerwerken = classificaties
            .Where(c => c.Classificatie.Type != VerzoekType.BuitenScope)
            .ToList();

        // Berichten waarvoor de AI-classificatie faalde zitten niet in classificaties en komen dus
        // ook niet in fase 2. Ze blijven ongelezen en komen elke poll terug — zonder pogingenteller
        // eeuwig, met een AI-call per poll. Ze worden hieronder alsnog geregistreerd zodat de teller
        // werkt. Bij een afgebroken batch (OpenAI-quota) is voor de resterende berichten géén poging
        // gedaan; die mogen niet meetellen, anders straft een quota-storing onschuldige berichten.
        var geclassificeerdeIds = classificaties
            .Select(c => c.Email.MessageId)
            .ToHashSet(StringComparer.Ordinal);
        List<InkomendBericht> mislukteClassificaties = classificationResult.AiAborted
            ? []
            : teClassificeren.Where(e => !geclassificeerdeIds.Contains(e.MessageId)).ToList();

        if (teVerwerken.Count == 0 && mislukteClassificaties.Count == 0)
        {
            var aantalBuitenScope = classificaties.Count(c => c.Classificatie.Type == VerzoekType.BuitenScope);
            log.LogInformation(
                "Alle {Aantal} emails buiten scope{Afgebroken} — geen verwerking in de database nodig",
                aantalBuitenScope,
                classificationResult.AiAborted ? " (AI batch vroegtijdig gestopt)" : "");
            return; // Fase 2 overslaan
        }

        // ── FASE 2: zwaar — alleen als er non-BuitenScope emails of classificatiefouten zijn ─────

        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            if (_databaseNoodmailVerstuurd)
            {
                _databaseNoodmailVerstuurd = false;
                log.LogInformation("Database weer bereikbaar — email processor hervat");
            }
            await SystemUtilities.AppSettings.LoadSettingsAsync(log);
        }
        catch (Exception dbEx)
        {
            if (!_databaseNoodmailVerstuurd)
            {
                log.LogError(dbEx, "Database niet beschikbaar — stuur noodmail");
                await StuurDatabaseNoodmailAsync(
                    graphService, teVerwerken.Count + mislukteClassificaties.Count, CategorizeerFout(dbEx), log);
            }
            else
            {
                log.LogWarning("Email processor gepauzeerd — database nog niet bereikbaar (noodmail al verstuurd)");
            }
            return;
        }

        // Onvoorwaardelijk herladen nu de DB wakker is: de hercheck vóór de INSERT hoort op een lijst
        // uit déze invocatie te gebeuren, niet op een kopie die tot de TTL oud kan zijn. (#709)
        await _uitsluitingslijst.HerlaadAsync(
            () => persistenceService.LaadUitgeslotenAdressenAsync(log), DateTime.UtcNow);

        // Teamlijst-gereedheid hoort HIER, niet in fase 1 (#700). Twee redenen, beide gevonden bij de
        // adversariële review:
        //  1. De check heeft de clubCode uit dbo.AppSettings nodig. In fase 1 is die cache op een verse
        //     worker nog leeg — de check zou dan altijd falen en terugkeren vóór de regel die de settings
        //     laadt. Dat zette de verwerking permanent stil, met een logregel die naar de verkeerde
        //     oorzaak wees.
        //  2. In fase 1 zou hij bij élke poll de database openen, ook bij een lege inbox. Azure SQL
        //     Serverless pauzeert pas na 60 minuten inactiviteit, dus dat houdt de database 24/7 wakker
        //     en verbruikt het gratis vCore-budget. Hier is de database toch al wakker.
        var gereedheid = context.InstanceServices.GetService<TeamlijstGereedheid>();
        if (gereedheid is not null
            && !await gereedheid.ZorgVoorTeamlijstAsync(SystemUtilities.AppSettings.GetOptionalClubCode()))
        {
            log.LogError(
                "Teamherkenning niet mogelijk (geen actieve teams voor deze club) — verwerking overgeslagen "
                + "om verkeerde koppelingen te voorkomen. Controleer of de Sportlink-sync heeft gedraaid.");
            return;
        }

        // Classificatiefouten vastleggen nu de database wakker is — anders bestaat er geen teller en
        // blijven deze berichten oneindig terugkomen (zie de toelichting bij mislukteClassificaties).
        foreach (var mislukt in mislukteClassificaties)
            await RegistreerClassificatieFoutAsync(mislukt, graphService, persistenceService, log);

        int verwerkt = 0, fouten = 0;

        foreach (var (email, classificatie) in teVerwerken)
        {
            try
            {
                await VerwerkEmailAsync(
                    email,
                    classificatie,
                    graphService,
                    _uitsluitingslijst.Adressen,
                    aiService,
                    persistenceService,
                    replyPolicyService,
                    log,
                    teamResolver);
                verwerkt++;
            }
            catch (Exception ex)
            {
                fouten++;
                log.LogError(ex, "Fout bij verwerken van email {MessageId} (onderwerp niet gelogd — AVG #210)",
                    email.MessageId);
                await LegVerwerkingsFoutVastAsync(
                    email, SanitizeFoutMelding(ex.Message), persistenceService, log);
            }
        }

        log.LogInformation(
            "Email verwerking afgerond: {Verwerkt} verwerkt, {Fouten} fouten, {ClassificatieFouten} classificatiefouten",
            verwerkt, fouten, mislukteClassificaties.Count);
    }

    /// <summary>
    /// Zorgt dat de uitsluitingslijst niet verouderd is en filtert de batch ermee — vóór er één
    /// bericht naar de AI-provider gaat (#709). Retourneert <c>null</c> als er niet geclassificeerd
    /// mag worden omdat de lijst nooit geladen is en ook nu niet te laden is (fail-closed, #423).
    /// </summary>
    internal static async Task<List<InkomendBericht>?> FilterMetVerseUitsluitingslijstAsync(
        List<InkomendBericht> teClassificeren,
        UitsluitingslijstCache uitsluitingslijst,
        Func<Task<HashSet<string>>> laadLijstAsync,
        EmailBatchFilterService batchFilterService,
        DateTime nuUtc,
        ILogger log)
    {
        var stand = await uitsluitingslijst.VerversIndienVerouderdAsync(laadLijstAsync, nuUtc, log);

        return stand switch
        {
            UitsluitingslijstStand.Ontbreekt => null,
            // Alleen na een verse lijst kan de uitkomst van het voorfilter achterhaald zijn.
            UitsluitingslijstStand.Ververst =>
                batchFilterService.FilterUitgeslotenAdressen(teClassificeren, uitsluitingslijst.Adressen),
            _ => teClassificeren
        };
    }

    private static async Task VerwerkEmailAsync(
        InkomendBericht email,
        BerichtClassificatie classificatie,
        IEmailGraphService graphService,
        IReadOnlySet<string> uitgeslotenAdressen,
        BerichtAiService aiService,
        IEmailPersistenceService persistenceService,
        EmailReplyPolicyService replyPolicyService,
        ILogger log,
        ITeamResolver teamResolver)
    {
        // Hercheck met verse DB-geladen uitsluitingslijst (kan afwijken van cache)
        if (uitgeslotenAdressen.Contains(email.Afzender))
        {
            log.LogInformation("Email {MessageId} van uitgesloten adres (verse lijst), overslaan (afzender niet gelogd — AVG #210)", email.MessageId);
            await graphService.MarkAsReadAsync(email.MessageId);
            return;
        }

        if (await BepaalVerwerkingIdAsync(email, graphService, persistenceService, log) is not int verwerkingId)
            return;

        // #323: reply-detectie — is dit een reply op een eerder door ons beantwoord bericht?
        if (!string.IsNullOrWhiteSpace(email.ConversationId))
        {
            var (isReply, origineleVerwerkingId, origineelType, originaleSamenvatting) =
                await persistenceService.DetecteerReplyOpOnsAntwoordAsync(email.ConversationId, log);

            if (isReply && origineleVerwerkingId.HasValue)
            {
                await persistenceService.UpdateReplyStatusAsync(verwerkingId, true, origineleVerwerkingId.Value);
                log.LogInformation("Email {Id} is reply op verwerking {OrigineleId}", verwerkingId, origineleVerwerkingId);

                // Detecteer of het een correctie is op de eerdere classificatie
                try
                {
                    var (isCorrectie, afgeleidType, correctieSamenvatting) = await aiService.DetecteerCorrectieAsync(
                        email.Body, email.Onderwerp, origineelType ?? "", originaleSamenvatting);

                    if (isCorrectie)
                    {
                        await persistenceService.InsertClassificatieCorrectieAsync(
                            origineleVerwerkingId.Value, verwerkingId,
                            origineelType ?? "", afgeleidType,
                            originaleSamenvatting, correctieSamenvatting);
                        log.LogInformation("Correctie gedetecteerd voor verwerking {OrigineleId}: {OrigineelType} → {JuistType}",
                            origineleVerwerkingId, origineelType, afgeleidType);
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Correctie-detectie mislukt voor reply {Id} — doorgaan zonder correctie", verwerkingId);
                }
            }
        }

        // #323: few-shot herclassificatie als er gevalideerde leermomenten zijn
        var voorbeelden = await persistenceService.HaalLeermomentVoorbeeldenOpAsync(log);
        if (voorbeelden.Count > 0)
        {
            try
            {
                classificatie = await aiService.ClassificeerBerichtAsync(
                    email.Body, email.Onderwerp, email.Afzender, voorbeelden);
                BerichtPipeline.ValideerDagDatum(classificatie, email.Body, email.Onderwerp);
                log.LogInformation("Email {Id} herclassificatie met {Aantal} leermomenten: {Type}",
                    verwerkingId, voorbeelden.Count, classificatie.Type);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Herclassificatie met leermomenten mislukt voor {Id} — originele classificatie behouden", verwerkingId);
            }
        }

        var classificatieJson = JsonConvert.SerializeObject(classificatie);

        if (await HandelBuitenScopeAsync(
                verwerkingId, email.MessageId, classificatie, classificatieJson, graphService, persistenceService, log))
            return;

        await persistenceService.UpdateStatusAsync(verwerkingId, EmailStatus.Geclassificeerd, classificatieJson);
        log.LogInformation("Email {Id} geregistreerd als {Type}, datum={Datum}",
            verwerkingId, classificatie.Type, classificatie.Datum);

        var plannerResponseJson = await BerichtPipeline.VerwerkMetPlannerAsync(
            classificatie, email, log, teamResolver);
        await persistenceService.UpdatePlannerResponseAsync(verwerkingId, plannerResponseJson);
        await persistenceService.UpdateStatusAsync(verwerkingId, EmailStatus.Verwerkt, null);

        var reviewMode = string.Equals(
            Environment.GetEnvironmentVariable("EmailReviewMode"), "true", StringComparison.OrdinalIgnoreCase);
        var replyUitkomst = await replyPolicyService.HandelReplyFlowAfAsync(
            verwerkingId,
            email,
            classificatie,
            plannerResponseJson,
            reviewMode,
            graphService,
            persistenceService,
            () => BerichtPipeline.BouwTemplateAntwoord(classificatie, plannerResponseJson, email, log),
            SanitizeFoutMelding,
            log);

        if (replyUitkomst != ReplyVerwerkingUitkomst.AntwoordVerstuurd)
            return;

        // Stuur interne notificatie naar teamleider bij herplanverzoeken van externe afzender (#66)
        if (classificatie.Type == VerzoekType.HerplanVerzoek
            && !string.IsNullOrWhiteSpace(classificatie.TeamNaam)
            && !string.IsNullOrWhiteSpace(classificatie.Datum))
        {
            await StuurTeamleiderNotificatieAsync(
                graphService, classificatie.TeamNaam, classificatie.Datum, log);
        }

        // Stuur vraag door naar coach bij team-contact-opvragen (#168)
        if (classificatie.Type == VerzoekType.TeamContactOpvragen
            && !string.IsNullOrWhiteSpace(classificatie.TeamNaam))
        {
            await StuurTeamContactBerichtDoorAsync(
                graphService, classificatie.TeamNaam, email, log);
        }
    }

    /// <summary>
    /// Bepaalt onder welk verwerkingId dit bericht verder verwerkt wordt: hergebruik van een
    /// niet-afgeronde rij, of een nieuwe rij. Retourneert <c>null</c> als de verwerking hier moet
    /// stoppen.
    /// <para>
    /// De guard kijkt naar de EINDSTATUS en niet naar het bestaan van de rij (#712): een rij met een
    /// niet-definitieve status betekent dat een eerdere poging is afgebroken, en die rij wordt dan
    /// hergebruikt.
    /// </para>
    /// </summary>
    internal static async Task<int?> BepaalVerwerkingIdAsync(
        InkomendBericht email,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        ILogger log)
    {
        var stand = await persistenceService.HaalVerwerkingStandOpAsync(email.MessageId);

        switch (EmailIdempotentie.Bepaal(stand))
        {
            case VerwerkingsBesluit.OverslaanAlAfgerond:
                log.LogInformation("Email {MessageId} is al definitief afgehandeld (status {Status}), overslaan",
                    email.MessageId, stand!.Status);
                await graphService.MarkAsReadAsync(email.MessageId);
                return null;

            case VerwerkingsBesluit.OpgevenNaMaxPogingen:
                await GeefVerwerkingOpAsync(
                    email, stand!.VerwerkingId, stand.Pogingen, "verwerking", graphService, persistenceService, log);
                return null;

            case VerwerkingsBesluit.OnbeslistNaVerzendPoging:
                await LegVoorBeoordelingNaVerzendPogingAsync(
                    email, stand!.VerwerkingId, graphService, persistenceService, log);
                return null;

            case VerwerkingsBesluit.HerhaalVerwerking:
                await persistenceService.VerhoogPogingenAsync(stand!.VerwerkingId);
                log.LogWarning(
                    "Email {MessageId} was niet afgerond (status {Status}) — verwerking {Id} wordt hervat, poging {Poging} van {Max}",
                    email.MessageId, stand.Status, stand.VerwerkingId, stand.Pogingen + 1, EmailIdempotentie.MaxPogingen);
                return stand.VerwerkingId;

            default:
                try
                {
                    // DB INSERT — classificatie is al gedaan in fase 1, resultaat wordt evt. verfijnd in fase 2
                    return await persistenceService.InsertEmailVerwerkingAsync(email);
                }
                catch (DubbeleMessageIdException)
                {
                    // Check-then-act: tussen het lezen van de stand en deze INSERT heeft een andere
                    // invocatie hetzelfde bericht geregistreerd. Dit mag géén fout worden: UpdateFoutAsync
                    // zoekt op MessageId en zou de rij van die andere verwerking op 'Fout' zetten, terwijl
                    // die het antwoord juist wél kan versturen. Bericht ongelezen laten — de andere
                    // verwerking handelt het af. (#707)
                    log.LogWarning(
                        "Email {MessageId} is gelijktijdig door een andere verwerking geregistreerd — deze poging stopt zonder foutstatus",
                        email.MessageId);
                    return null;
                }
        }
    }

    /// <summary>
    /// Handelt een buiten-scope classificatie af: status vastleggen, labelen, als gelezen markeren,
    /// géén antwoord. Retourneert <c>true</c> als het bericht hiermee is afgehandeld.
    /// <para>
    /// Nodig omdat de herclassificatie met leermomenten alsnog 'buiten scope' kan opleveren. Zo'n
    /// uitkomst kwam niet meer langs het BuitenScope-voorfilter van fase 1, waardoor er tóch een
    /// automatisch "vereist handmatige afhandeling"-antwoord uitging: identieke input, ander gedrag,
    /// puur afhankelijk van of er gevalideerde leermomenten in de database staan. (#712)
    /// </para>
    /// </summary>
    internal static async Task<bool> HandelBuitenScopeAsync(
        int verwerkingId,
        string messageId,
        BerichtClassificatie classificatie,
        string classificatieJson,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        ILogger log)
    {
        if (classificatie.Type != VerzoekType.BuitenScope)
            return false;

        await persistenceService.UpdateStatusAsync(verwerkingId, EmailStatus.BuitenScope, classificatieJson);
        log.LogInformation("Email {Id} buiten scope na herclassificatie — geen antwoord verstuurd", verwerkingId);
        await LabelBuitenScopeAsync(graphService, messageId, log);
        return true;
    }

    /// <summary>
    /// Labelt een buiten-scope bericht in Outlook en markeert het als gelezen. Zelfde eindresultaat
    /// als het voorfilter van fase 1. Labelen mag niet fataal zijn: de status in de database is al
    /// definitief, dus een volgende poll slaat het bericht over en markeert het dan als gelezen.
    /// </summary>
    private static async Task LabelBuitenScopeAsync(
        IEmailGraphService graphService, string messageId, ILogger log)
    {
        try
        {
            await graphService.EnsureMasterCategoryAsync(GeenAiAntwoordLabel, "preset0");
            await graphService.SetCategoriesAsync(messageId, GeenAiAntwoordLabel);
            await graphService.MarkAsReadAsync(messageId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Outlook-labeling buiten scope mislukt voor {MessageId} — status is wel vastgelegd", messageId);
        }
    }

    /// <summary>
    /// Geeft een bericht definitief op na <see cref="EmailIdempotentie.MaxPogingen"/> mislukte
    /// pogingen: status Fout met een verklarende melding, en als gelezen markeren zodat het de
    /// wachtrij van 10 oudste ongelezen berichten niet blijft bezetten. Er is nooit een antwoord
    /// verstuurd op dit pad, dus de coördinator moet het bericht handmatig oppakken — het email-log
    /// is daarvoor het spoor.
    /// </summary>
    private static async Task GeefVerwerkingOpAsync(
        InkomendBericht email,
        int verwerkingId,
        int pogingen,
        string fase,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        ILogger log)
    {
        log.LogError(
            "Email {MessageId} opgegeven na {Pogingen} mislukte pogingen ({Fase}) — als gelezen gemarkeerd "
            + "zodat de wachtrij niet blokkeert. Handmatige opvolging nodig via het email-log.",
            email.MessageId, pogingen, fase);

        try
        {
            await persistenceService.UpdateFoutAsync(verwerkingId,
                $"Opgegeven na {pogingen} mislukte pogingen ({fase}) — handmatige opvolging nodig");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon opgeven-status niet vastleggen voor verwerking {Id}", verwerkingId);
        }

        await graphService.MarkAsReadAsync(email.MessageId);
    }

    /// <summary>
    /// Legt een verwerkingsfout vast voor een bericht waarvan het verwerkingId op deze plek niet bekend
    /// is: de fout kan overal in de verwerking zijn opgetreden, ook vóór de rij bestond.
    /// <para>
    /// Sinds #717 muteert de foutafhandeling op <c>Id</c> en niet meer op MessageId, dus wordt het Id
    /// hier eerst opgezocht. Bestaat er geen rij, dan is er ook niets vast te leggen — de fout is dan al
    /// gelogd en het bericht blijft ongelezen voor de volgende poll.
    /// </para>
    /// </summary>
    private static async Task LegVerwerkingsFoutVastAsync(
        InkomendBericht email,
        string foutMelding,
        IEmailPersistenceService persistenceService,
        ILogger log)
    {
        try
        {
            var stand = await persistenceService.HaalVerwerkingStandOpAsync(email.MessageId);
            if (stand is null)
            {
                log.LogWarning(
                    "Email {MessageId}: verwerking mislukt vóórdat er een rij bestond — geen foutstatus vastgelegd",
                    email.MessageId);
                return;
            }

            await persistenceService.UpdateFoutAsync(stand.VerwerkingId, foutMelding);
        }
        catch (Exception ex)
        {
            // Een fout bij het vastleggen van een fout mag nooit cascaderen: de oorspronkelijke fout is
            // al gelogd en het bericht blijft ongelezen voor de volgende poll.
            log.LogWarning(ex, "Kon foutstatus niet vastleggen voor {MessageId}", email.MessageId);
        }
    }

    /// <summary>
    /// Handelt een verwerking af waarvan de verzenduitkomst onbekend is (#716): niet opnieuw versturen,
    /// maar op <see cref="EmailStatus.Review"/> zetten en als gelezen markeren.
    /// <para>
    /// Opnieuw versturen zou de afzender een tweede antwoord kunnen geven; niets doen zou het bericht
    /// elke poll laten terugkomen en de wachtrij van tien oudste ongelezen berichten bezetten. De
    /// coördinator ziet het bericht terug in het email-log met status Review en kan zelf vaststellen of
    /// er al een antwoord is aangekomen.
    /// </para>
    /// </summary>
    private static async Task LegVoorBeoordelingNaVerzendPogingAsync(
        InkomendBericht email,
        int verwerkingId,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        ILogger log)
    {
        log.LogWarning(
            "Email {MessageId}: er staat een verzendpoging vastgelegd zonder bekende uitkomst voor verwerking "
            + "{Id} — waarschijnlijk is een eerdere invocatie afgebroken tussen versturen en vastleggen. "
            + "Er wordt NIET opnieuw verstuurd; status op Review voor beoordeling via het email-log.",
            email.MessageId, verwerkingId);

        try
        {
            await persistenceService.UpdateStatusAsync(verwerkingId, EmailStatus.Review, null);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon Review-status niet vastleggen voor verwerking {Id}", verwerkingId);
        }

        try
        {
            await graphService.EnsureMasterCategoryAsync(GeenAiAntwoordLabel, "preset0");
            await graphService.SetCategoriesAsync(email.MessageId, GeenAiAntwoordLabel);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Outlook-labeling mislukt voor verwerking {Id}", verwerkingId);
        }

        await graphService.MarkAsReadAsync(email.MessageId);
    }

    /// <summary>
    /// Legt een mislukte AI-classificatie vast en verhoogt de pogingenteller.
    /// <para>
    /// Zonder rij in de database is er geen spoor én geen teller: het bericht blijft ongelezen, komt
    /// elke poll terug en kost elke keer opnieuw een AI-call. Tien zulke berichten blokkeren de hele
    /// wachtrij. Op dit pad wordt nooit iets verstuurd, dus een extra poging kan geen dubbel antwoord
    /// veroorzaken. (#712)
    /// </para>
    /// </summary>
    internal static async Task RegistreerClassificatieFoutAsync(
        InkomendBericht email,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        ILogger log)
    {
        try
        {
            var stand = await persistenceService.HaalVerwerkingStandOpAsync(email.MessageId);
            int verwerkingId;

            switch (EmailIdempotentie.Bepaal(stand))
            {
                case VerwerkingsBesluit.OverslaanAlAfgerond:
                    log.LogInformation(
                        "Email {MessageId}: classificatie mislukt maar verwerking is al definitief afgehandeld — als gelezen gemarkeerd",
                        email.MessageId);
                    await graphService.MarkAsReadAsync(email.MessageId);
                    return;

                case VerwerkingsBesluit.OpgevenNaMaxPogingen:
                    await GeefVerwerkingOpAsync(
                        email, stand!.VerwerkingId, stand.Pogingen, "AI-classificatie",
                        graphService, persistenceService, log);
                    return;

                case VerwerkingsBesluit.OnbeslistNaVerzendPoging:
                    // Kan alleen als een eerdere poging al aan het versturen toe was. Dan is opnieuw
                    // classificeren zinloos en opnieuw versturen onveilig. (#716)
                    await LegVoorBeoordelingNaVerzendPogingAsync(
                        email, stand!.VerwerkingId, graphService, persistenceService, log);
                    return;

                case VerwerkingsBesluit.HerhaalVerwerking:
                    await persistenceService.VerhoogPogingenAsync(stand!.VerwerkingId);
                    verwerkingId = stand.VerwerkingId;
                    break;

                default:
                    verwerkingId = await persistenceService.InsertEmailVerwerkingAsync(email);
                    break;
            }

            await persistenceService.UpdateFoutAsync(verwerkingId, "AI-classificatie mislukt");
            log.LogWarning(
                "Email {MessageId}: AI-classificatie mislukt — poging {Poging} van {Max} vastgelegd, bericht blijft ongelezen voor de volgende poll",
                email.MessageId, (stand?.Pogingen ?? 0) + 1, EmailIdempotentie.MaxPogingen);
        }
        catch (DubbeleMessageIdException)
        {
            // Een andere invocatie registreerde het bericht tussen de stand-lezing en deze INSERT.
            // Geen fout: die verwerking is leidend en heeft een eigen pogingenteller. (#707)
            log.LogInformation(
                "Email {MessageId}: al door een andere verwerking geregistreerd — classificatiefout niet vastgelegd",
                email.MessageId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon mislukte AI-classificatie van {MessageId} niet vastleggen", email.MessageId);
        }
    }

    private static async Task StuurTeamleiderNotificatieAsync(
        IEmailGraphService graphService, string teamNaam, string datum, ILogger log)
    {
        try
        {
            var teamleider = await SportlinkFunction.Planner.PlannerDataAccess.GetTeamleiderContactAsync(teamNaam);
            if (teamleider == null)
            {
                log.LogInformation("Geen teamleider gevonden voor {Team} in avg.Teambegeleiding — notificatie overgeslagen", teamNaam);
                return;
            }

            var plannerNaam = SystemUtilities.AppSettings.GetSetting("plannerAfzenderNaam")
                ?? throw new InvalidOperationException("Vereiste instelling 'plannerAfzenderNaam' ontbreekt in dbo.AppSettings");

            DateOnly.TryParse(datum, out var datumDate);
            var datumDisplay = datumDate != default
                ? datumDate.ToString("dddd d MMMM yyyy", new System.Globalization.CultureInfo("nl-NL"))
                : datum;

            var notificatieBody = $"Hoi {teamleider.Naam},\n\n"
                + $"Er is een herplanverzoek ontvangen voor {teamNaam} op {datumDisplay}.\n\n"
                + $"De coördinator heeft automatisch gereageerd op dit verzoek. "
                + $"Je hoeft zelf geen actie te ondernemen, maar we willen je op de hoogte houden.\n\n"
                + $"Als je vragen hebt over dit herplanverzoek, neem dan contact op met de veldplanner.\n\n"
                + $"Met vriendelijke groet,\n{plannerNaam}";

            await graphService.SendReplyAsync(
                teamleider.Emailadres,
                $"Herplanverzoek ontvangen voor {teamNaam} op {datumDisplay}",
                notificatieBody,
                null);

            log.LogInformation("Teamleider-notificatie verstuurd voor team {Team}", teamNaam);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Fout bij versturen teamleider-notificatie voor {Team} — hoofdverwerking niet onderbroken", teamNaam);
        }
    }

    private static async Task StuurTeamContactBerichtDoorAsync(
        IEmailGraphService graphService, string teamNaam, InkomendBericht email, ILogger log)
    {
        try
        {
            var coach = await SportlinkFunction.Planner.PlannerDataAccess.GetTeamleiderContactAsync(teamNaam);
            if (coach == null)
            {
                log.LogInformation("Geen coach gevonden voor {Team} — doorsturen overgeslagen", teamNaam);
                return;
            }

            // AVG-maatregel: BCC-audit-kopie naar de veldplanner bij het doorsturen van
            // persoonsgegevens. Stond eerder op de sleutel 'coordinatorEmail' — die bestaat niet in
            // dbo.AppSettings, dus de kopie ging nóóit uit terwijl code en documentatie dat wel
            // beloofden. De juiste sleutel is 'plannerEmailAdres' (kolom PlannerEmailAdres). (#712)
            var auditKopieAdres = SystemUtilities.AppSettings.GetSetting("plannerEmailAdres");
            if (string.IsNullOrWhiteSpace(auditKopieAdres))
            {
                // Stil overslaan mag niet: de audit-kopie is een AVG-maatregel, geen nice-to-have.
                auditKopieAdres = null;
                log.LogWarning(
                    "Instelling 'plannerEmailAdres' ontbreekt of is leeg — teambegeleidingsvraag voor {Team} wordt "
                    + "doorgestuurd ZONDER BCC-audit-kopie. Vul het e-mailadres van de veldplanner in bij Instellingen.",
                    teamNaam);
            }

            var subject = $"[{teamNaam}] vraag van {email.AfzenderNaam}";
            var body = $"Er is een vraag binnengekomen over de begeleiding van {teamNaam}.\n\n"
                     + $"Vraag van: {email.AfzenderNaam}\n\n"
                     + $"---\n{email.Body}\n---\n\n"
                     + "U kunt direct antwoorden op dit bericht — uw antwoord gaat naar de vraagsteller.";

            // AVG: Reply-To = email.Afzender zodat coach rechtstreeks kan antwoorden
            // BCC veldplanner voor audit; coach-email nooit in logs
            await graphService.StuurTeamContactDoorAsync(
                [coach.Emailadres], subject, body, email.Afzender, auditKopieAdres);

            log.LogInformation("Teambegeleiding-vraag doorgestuurd voor {Team}", teamNaam);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Fout bij doorsturen teambegeleiding-vraag voor {Team} — hoofdverwerking niet onderbroken", teamNaam);
        }
    }

    /// <summary>
    /// Stuurt een noodmail als de database niet beschikbaar is.
    /// Emails blijven ongelezen in de inbox en worden bij de volgende poll opnieuw opgepikt.
    /// </summary>
    private static async Task StuurDatabaseNoodmailAsync(
        IEmailGraphService graphService, int aantalEmails, string foutmelding, ILogger log)
    {
        var mailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
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
            _databaseNoodmailVerstuurd = true;
            log.LogWarning("Noodmail verstuurd naar {Mailbox} — processor gepauzeerd tot database weer bereikbaar", mailbox);
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

    private static async Task StuurOpenAiNoodmailAsync(
        IEmailGraphService graphService, string foutmelding, ILogger log)
    {
        var mailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
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
            _openAiQuotaNoodmailVerstuurdenOp = DateTime.UtcNow;
            log.LogWarning("OpenAI quota-noodmail verstuurd naar {Mailbox}", mailbox);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon OpenAI quota-noodmail niet versturen");
        }
    }

}
