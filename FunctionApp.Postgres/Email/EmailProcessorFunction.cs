using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Monitoring;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Processing;
using FunctionApp.Postgres.TeamResolution;
using Planner.Shared;

namespace FunctionApp.Postgres.Email;

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
    /// Er is een verzendpoging vastgelegd waarvan de uitkomst onbekend is. Niet opnieuw
    /// versturen — het eerste antwoord kan de deur al uit zijn — maar ter beoordeling neerleggen.
    /// </summary>
    OnbeslistNaVerzendPoging
}

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/EmailProcessorFunction.cs</c> (#972) —
/// idempotentiebesluit voor de e-mailverwerking. Woordelijke kopie: puur en zonder
/// afhankelijkheden, zodat elk faalscenario los te testen is.
/// <para>
/// Het bestaan van een rij in <c>planner.emailverwerking</c> betekende voorheen "al verwerkt".
/// Dat is fout: de rij wordt aangemaakt vóór de verwerking, dus élke fout daarna liet een rij
/// achter waardoor de volgende poll het bericht oversloeg én als gelezen markeerde. Het besluit
/// hangt daarom af van de <b>eindstatus</b>, niet van het bestaan van de rij.
/// </para>
/// </summary>
internal static class EmailIdempotentie
{
    /// <summary>
    /// Maximaal aantal verwerkingspogingen per bericht. Drie is genoeg om tijdelijke fouten (Graph
    /// 429/503, een net herstelde database, een time-out) uit te zitten, en laag genoeg dat een
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
        // alsnog opnieuw verstuurd worden.
        if (stand.VerzendPogingOnbeslist)
            return VerwerkingsBesluit.OnbeslistNaVerzendPoging;

        return stand.Pogingen >= MaxPogingen
            ? VerwerkingsBesluit.OpgevenNaMaxPogingen
            : VerwerkingsBesluit.HerhaalVerwerking;
    }
}

/// <summary>Uitkomst van een poging om de uitsluitingslijst te verversen.</summary>
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
/// In-memory kopie van de uitsluitingslijst met een geldigheidsduur. Woordelijke kopie van het
/// SQL Server-origineel.
/// </summary>
internal sealed class UitsluitingslijstCache
{
    /// <summary>
    /// Geldigheidsduur van de kopie. Bewust ruimer dan het poll-interval: bij élke poll herladen zou
    /// de database wakker houden voor batches die anders helemaal niet in de database terechtkomen.
    /// Vijftien minuten begrenst hoe lang een net uitgesloten adres nog een AI-call kan kosten.
    /// </summary>
    internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    // volatile / Volatile.Read: meerdere invocaties lezen dezelfde statische instantie.
    private volatile HashSet<string> _adressen = new(StringComparer.OrdinalIgnoreCase);
    private long _geladenOpTicksUtc;

    internal IReadOnlySet<string> Adressen => _adressen;

    /// <summary>Is de lijst ooit met succes geladen? Zo niet, dan geldt fail-closed.</summary>
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

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/EmailProcessorFunction.cs</c> (#972) — de
/// mailbox-getriggerde e-mailverwerkingspijplijn die op deze tier tot nu toe volledig ontbrak.
/// Sinds de productiecutover naar Postgres op 2026-09-04 draait deze code, maar zonder deze
/// functie werd de mailbox nooit gepolld — geen classificatie, geen auto-reply.
///
/// <para>
/// <b>Structurele afwijkingen t.o.v. het SQL Server-origineel — alle drie al gedocumenteerd op
/// <c>BerichtPipeline</c>-niveau (item 1-3, buiten scope van deze hotfix) plus twee nieuw
/// ontdekte, hier expliciet vastgelegde afwijkingen (item 4-5):</b>
/// </para>
/// <list type="number">
/// <item>Opponent-lookup (<c>FindMatchByOpponentAsync</c>) niet vertaald — zie <c>BerichtPipeline</c>.</item>
/// <item><c>TeamContactOpvragen</c> geeft in het auto-reply-antwoord altijd <c>coachGevonden = false</c>
/// — zie <c>BerichtPipeline</c>. De vervolgnotificatie hieronder (<see cref="StuurTeamContactBerichtDoorAsync"/>)
/// gebruikt een ANDERE, wél bestaande databron (<c>avg.teambegeleiding</c> via
/// <c>AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync</c>) en werkt dus wél.</item>
/// <item>KNVB-PDF-bijlage/"verzet zonder datum" niet vertaald — zie <c>BerichtPipeline</c> en
/// <c>EmailReplyPolicyService</c>.</item>
/// <item><b>Nieuw bij #972:</b> de teamleider-/teamcontact-vervolgnotificaties (#66/#168) gebruiken
/// hier <see cref="AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync"/> in plaats van
/// <c>PlannerDataAccess.GetTeamleiderContactAsync</c> (bestaat hier niet) — dat levert alleen een
/// e-mailadres, geen naam, dus de notificatietekst gebruikt een generieke aanhef in plaats van
/// "Hoi {naam},".</item>
/// <item><b>Nieuw bij #972:</b> <see cref="INoodmailThrottleStore"/> is wél vertaald (Azure Table
/// Storage is DB-tier-agnostisch), maar de onafhankelijke, ARM-gebaseerde database-uitvalmonitor
/// (<c>DatabaseUitvalMonitorFunction</c>/<c>IDatabaseStatusReader</c>, #831) is dat niet — die
/// controleert specifiek Azure SQL-status en is een apart, niet-#972-issue.</item>
/// </list>
/// </summary>
public class EmailProcessorFunction
{
    // Throttle-sleutels voor INoodmailThrottleStore. Geen gedeelde sleutel met een database-
    // uitvalmonitor op deze tier (zie klassekop, item 5) — de literal staat daarom hier vast in
    // plaats van naar zo'n monitorklasse te verwijzen.
    internal const string DatabaseNoodmailSleutel = "database-noodmail";
    internal const string OpenAiQuotaNoodmailSleutel = "openai-quota-noodmail";
    private static readonly TimeSpan OpenAiQuotaNoodmailInterval = TimeSpan.FromHours(24);

    // Uitsluitingslijst: geladen vóór elke AI-classificatie (fail-closed bij cold start).
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

        // IEmailGraphService is alleen geregistreerd als Graph geconfigureerd is én
        // EgressGuard.ExternalIntegrationsAllowed() true is (Program.cs, #857) — buiten productie
        // (lokaal, CI) is dit onvoorwaardelijk null. Dat is verwacht, correct gedrag: nooit
        // gooien, gewoon overslaan.
        var graphService = context.InstanceServices.GetService<IEmailGraphService>();
        if (graphService == null)
        {
            log.LogError("GraphServiceClient niet beschikbaar — controleer Graph settings");
            return;
        }

        var loggerFactory = context.InstanceServices.GetRequiredService<ILoggerFactory>();
        var throttleStore = context.InstanceServices.GetRequiredService<INoodmailThrottleStore>();
        var cs = PostgresDatabaseConfig.ConnectionString;

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
        // uitgesloten mag niet alsnog naar de externe AI-provider gaan.
        var verseBatch = await FilterMetVerseUitsluitingslijstAsync(
            teClassificeren,
            _uitsluitingslijst,
            async () =>
            {
                await PostgresSystemUtilities.WaitForDatabaseAsync(log);
                await PostgresAppSettings.LoadSettingsAsync(log);
                return await LaadUitgeslotenAdressenAsync(cs, PostgresClubScope.Primary, log);
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

        // IChatClient is alleen geregistreerd als OpenAiApiKey geconfigureerd is én EgressGuard dat
        // toestaat (#857) — buiten productie onvoorwaardelijk null. Nooit gooien: loggen en stoppen,
        // exact hetzelfde patroon als graphService hierboven.
        var chatClient = context.InstanceServices.GetService<IChatClient>();
        if (chatClient is null)
        {
            log.LogError("IChatClient niet beschikbaar — controleer OpenAiApiKey/EgressGuard-configuratie; verwerking overgeslagen");
            return;
        }

        var aiService = new BerichtAiService(loggerFactory.CreateLogger<BerichtAiService>(), chatClient);
        var classificationResult = await classificationService.ClassificeerBatchAsync(
            teClassificeren,
            email => aiService.ClassificeerBerichtAsync(email.Body ?? "", email.Onderwerp, email.Afzender),
            IsOpenAiQuotaFout,
            log);

        if (classificationResult.AiAborted && classificationResult.QuotaException != null)
        {
            var quotaEx = classificationResult.QuotaException;
            if (await MoetOpenAiQuotaNoodmailVersturenAsync(throttleStore, DateTime.UtcNow))
            {
                await StuurOpenAiNoodmailAsync(graphService, CategorizeerFout(quotaEx), throttleStore, log);
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
        // ook niet in fase 2. Ze blijven ongelezen en komen elke poll terug. Ze worden hieronder
        // alsnog geregistreerd zodat de pogingenteller werkt. Bij een afgebroken batch
        // (OpenAI-quota) is voor de resterende berichten géén poging gedaan; die mogen niet
        // meetellen, anders straft een quota-storing onschuldige berichten.
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

        string clubCode;
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            await BehandelDatabaseHerstelAsync(throttleStore, log);
            await PostgresAppSettings.LoadSettingsAsync(log);
            clubCode = PostgresClubScope.Primary;
        }
        catch (Exception dbEx)
        {
            await BehandelDatabaseVerbindingsFoutAsync(
                dbEx, graphService, teVerwerken.Count + mislukteClassificaties.Count, throttleStore, log);
            return;
        }

        // Onvoorwaardelijk herladen nu de DB wakker is: de hercheck vóór de INSERT hoort op een lijst
        // uit déze invocatie te gebeuren, niet op een kopie die tot de TTL oud kan zijn.
        await _uitsluitingslijst.HerlaadAsync(
            () => LaadUitgeslotenAdressenAsync(cs, clubCode, log), DateTime.UtcNow);

        // Teamnaam→canonieke-teamlijst-verversing hoort hier, niet in fase 1: idempotent, kan
        // zonder bezwaar herhaald worden — zelfde pad als EmailTestFunction/AdminTeamsHerstelFunction.
        await TeamCanonicalisatieService.RefreshAsync(cs, clubCode, log);
        var teamResolver = new TeamResolver(new TeamCandidateRepository(cs));

        // Classificatiefouten vastleggen nu de database wakker is — anders bestaat er geen teller en
        // blijven deze berichten oneindig terugkomen (zie de toelichting bij mislukteClassificaties).
        foreach (var mislukt in mislukteClassificaties)
            await RegistreerClassificatieFoutAsync(cs, clubCode, mislukt, graphService, log);

        int verwerkt = 0, fouten = 0;

        foreach (var (email, classificatie) in teVerwerken)
        {
            try
            {
                await VerwerkEmailAsync(
                    cs,
                    clubCode,
                    email,
                    classificatie,
                    graphService,
                    _uitsluitingslijst.Adressen,
                    aiService,
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
                    cs, email, SanitizeFoutMelding(ex.Message), log);
            }
        }

        log.LogInformation(
            "Email verwerking afgerond: {Verwerkt} verwerkt, {Fouten} fouten, {ClassificatieFouten} classificatiefouten",
            verwerkt, fouten, mislukteClassificaties.Count);
    }

    /// <summary>
    /// Zorgt dat de uitsluitingslijst niet verouderd is en filtert de batch ermee — vóór er één
    /// bericht naar de AI-provider gaat. Retourneert <c>null</c> als er niet geclassificeerd mag
    /// worden omdat de lijst nooit geladen is en ook nu niet te laden is (fail-closed).
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

    private static async Task<HashSet<string>> LaadUitgeslotenAdressenAsync(string cs, string clubCode, ILogger log)
    {
        var adressen = await SqlEmailPersistenceRepository.GetExcludedEmailAddressesAsync(cs, clubCode);
        log.LogInformation("Uitsluitingslijst geladen: {Aantal} adressen", adressen.Count);
        return adressen;
    }

    private static async Task VerwerkEmailAsync(
        string cs,
        string clubCode,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        IEmailGraphService graphService,
        IReadOnlySet<string> uitgeslotenAdressen,
        BerichtAiService aiService,
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

        if (await BepaalVerwerkingIdAsync(cs, clubCode, email, graphService, log) is not int verwerkingId)
            return;

        // Reply-detectie — is dit een reply op een eerder door ons beantwoord bericht?
        await DetecteerEnRegistreerCorrectieAsync(cs, clubCode, verwerkingId, email, aiService, log);

        // Few-shot herclassificatie als er gevalideerde leermomenten zijn
        var voorbeelden = await LearningMomentRepository.HaalVoorbeeldenOpAsync(cs, clubCode, log);
        if (voorbeelden.Count > 0)
        {
            try
            {
                classificatie = await aiService.ClassificeerBerichtAsync(
                    email.Body ?? "", email.Onderwerp, email.Afzender, voorbeelden);
                BerichtPipeline.ValideerDagDatum(classificatie, email.Body ?? "", email.Onderwerp);
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
                cs, verwerkingId, email.MessageId, classificatie, classificatieJson, graphService, log))
            return;

        await SqlEmailPersistenceRepository.UpdateStatusAsync(cs, verwerkingId, EmailStatus.Geclassificeerd, classificatieJson);
        log.LogInformation("Email {Id} geregistreerd als {Type}, datum={Datum}",
            verwerkingId, classificatie.Type, classificatie.Datum);

        var plannerResponseJson = await BerichtPipeline.VerwerkMetPlannerAsync(
            classificatie, email, log, teamResolver, clubCode);
        await SqlEmailPersistenceRepository.UpdatePlannerResponseAsync(cs, verwerkingId, plannerResponseJson);
        await SqlEmailPersistenceRepository.UpdateStatusAsync(cs, verwerkingId, EmailStatus.Verwerkt, null);

        var reviewMode = string.Equals(
            Environment.GetEnvironmentVariable("EmailReviewMode"), "true", StringComparison.OrdinalIgnoreCase);
        var reviewRecipient = Environment.GetEnvironmentVariable("EmailReviewRecipient");
        var replyUitkomst = await replyPolicyService.HandelReplyFlowAfAsync(
            cs,
            verwerkingId,
            email,
            classificatie,
            plannerResponseJson,
            reviewMode,
            reviewRecipient,
            graphService,
            () => BerichtPipeline.BouwTemplateAntwoord(classificatie, plannerResponseJson, email, log, null, clubCode),
            SanitizeFoutMelding,
            log);

        if (replyUitkomst != ReplyVerwerkingUitkomst.AntwoordVerstuurd)
            return;

        await StuurVervolgNotificatiesAsync(cs, clubCode, classificatie, email, graphService, log);
    }

    /// <summary>
    /// Detecteert of dit bericht een reply is op een eerder door ons beantwoord bericht en, zo ja,
    /// of de afzender daarin een correctie geeft op de eerdere classificatie. Faalt de
    /// correctie-detectie zelf, dan gaat de hoofdverwerking gewoon door zonder correctie.
    /// </summary>
    private static async Task DetecteerEnRegistreerCorrectieAsync(
        string cs,
        string clubCode,
        int verwerkingId,
        InkomendBericht email,
        BerichtAiService aiService,
        ILogger log)
    {
        if (string.IsNullOrWhiteSpace(email.ConversationId))
            return;

        var (isReply, origineleVerwerkingId, origineelType, originaleSamenvatting) =
            await SqlEmailPersistenceRepository.DetecteerReplyOpOnsAntwoordAsync(cs, email.ConversationId, clubCode, log);

        if (!isReply || !origineleVerwerkingId.HasValue)
            return;

        await SqlEmailPersistenceRepository.UpdateReplyStatusAsync(cs, verwerkingId, true, origineleVerwerkingId.Value);
        log.LogInformation("Email {Id} is reply op verwerking {OrigineleId}", verwerkingId, origineleVerwerkingId);

        // Detecteer of het een correctie is op de eerdere classificatie
        try
        {
            var (isCorrectie, afgeleidType, correctieSamenvatting) = await aiService.DetecteerCorrectieAsync(
                email.Body ?? "", email.Onderwerp, origineelType ?? "", originaleSamenvatting);

            if (isCorrectie)
            {
                await LearningMomentRepository.InsertClassificatieCorrectieAsync(
                    cs, origineleVerwerkingId.Value, verwerkingId,
                    origineelType ?? "", afgeleidType,
                    originaleSamenvatting, correctieSamenvatting, clubCode);
                log.LogInformation("Correctie gedetecteerd voor verwerking {OrigineleId}: {OrigineelType} → {JuistType}",
                    origineleVerwerkingId, origineelType, afgeleidType);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Correctie-detectie mislukt voor reply {Id} — doorgaan zonder correctie", verwerkingId);
        }
    }

    /// <summary>
    /// Stuurt de interne vervolgnotificaties die horen bij een succesvol verstuurd antwoord:
    /// teamleider-notificatie bij een herplanverzoek (#66) en het doorsturen van een
    /// team-contact-vraag naar de begeleider (#168). Zie de klassekop (item 4) voor de
    /// afwijking t.o.v. het SQL Server-origineel.
    /// </summary>
    private static async Task StuurVervolgNotificatiesAsync(
        string cs,
        string clubCode,
        BerichtClassificatie classificatie,
        InkomendBericht email,
        IEmailGraphService graphService,
        ILogger log)
    {
        if (classificatie.Type == VerzoekType.HerplanVerzoek
            && !string.IsNullOrWhiteSpace(classificatie.TeamNaam)
            && !string.IsNullOrWhiteSpace(classificatie.Datum))
        {
            await StuurTeamleiderNotificatieAsync(cs, clubCode, graphService, classificatie.TeamNaam!, classificatie.Datum!, log);
        }

        if (classificatie.Type == VerzoekType.TeamContactOpvragen
            && !string.IsNullOrWhiteSpace(classificatie.TeamNaam))
        {
            await StuurTeamContactBerichtDoorAsync(cs, clubCode, graphService, classificatie.TeamNaam!, email, log);
        }
    }

    /// <summary>
    /// Bepaalt onder welk verwerkingId dit bericht verder verwerkt wordt: hergebruik van een
    /// niet-afgeronde rij, of een nieuwe rij. Retourneert <c>null</c> als de verwerking hier moet
    /// stoppen.
    /// </summary>
    internal static async Task<int?> BepaalVerwerkingIdAsync(
        string cs,
        string clubCode,
        InkomendBericht email,
        IEmailGraphService graphService,
        ILogger log)
    {
        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(cs, email.MessageId);

        switch (EmailIdempotentie.Bepaal(stand))
        {
            case VerwerkingsBesluit.OverslaanAlAfgerond:
                log.LogInformation("Email {MessageId} is al definitief afgehandeld (status {Status}), overslaan",
                    email.MessageId, stand!.Status);
                await graphService.MarkAsReadAsync(email.MessageId);
                return null;

            case VerwerkingsBesluit.OpgevenNaMaxPogingen:
                await GeefVerwerkingOpAsync(
                    cs, email, stand!.VerwerkingId, stand.Pogingen, "verwerking", graphService, log);
                return null;

            case VerwerkingsBesluit.OnbeslistNaVerzendPoging:
                await LegVoorBeoordelingNaVerzendPogingAsync(
                    cs, email, stand!.VerwerkingId, graphService, log);
                return null;

            case VerwerkingsBesluit.HerhaalVerwerking:
                await SqlEmailPersistenceRepository.VerhoogPogingenAsync(cs, stand!.VerwerkingId);
                log.LogWarning(
                    "Email {MessageId} was niet afgerond (status {Status}) — verwerking {Id} wordt hervat, poging {Poging} van {Max}",
                    email.MessageId, stand.Status, stand.VerwerkingId, stand.Pogingen + 1, EmailIdempotentie.MaxPogingen);
                return stand.VerwerkingId;

            default:
                try
                {
                    return await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(cs, email, clubCode);
                }
                catch (DubbeleMessageIdException)
                {
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
    /// </summary>
    internal static async Task<bool> HandelBuitenScopeAsync(
        string cs,
        int verwerkingId,
        string messageId,
        BerichtClassificatie classificatie,
        string classificatieJson,
        IEmailGraphService graphService,
        ILogger log)
    {
        if (classificatie.Type != VerzoekType.BuitenScope)
            return false;

        await SqlEmailPersistenceRepository.UpdateStatusAsync(cs, verwerkingId, EmailStatus.BuitenScope, classificatieJson);
        log.LogInformation("Email {Id} buiten scope na herclassificatie — geen antwoord verstuurd", verwerkingId);
        await LabelBuitenScopeAsync(graphService, messageId, log);
        return true;
    }

    private static async Task LabelBuitenScopeAsync(
        IEmailGraphService graphService, string messageId, ILogger log)
    {
        try
        {
            await graphService.EnsureMasterCategoryAsync(EmailCategorieLabels.GeenAiAntwoord, EmailCategorieLabels.GeenAiAntwoordKleur);
            await graphService.SetCategoriesAsync(messageId, EmailCategorieLabels.GeenAiAntwoord);
            await graphService.MarkAsReadAsync(messageId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Outlook-labeling buiten scope mislukt voor {MessageId} — status is wel vastgelegd", messageId);
        }
    }

    private static async Task GeefVerwerkingOpAsync(
        string cs,
        InkomendBericht email,
        int verwerkingId,
        int pogingen,
        string fase,
        IEmailGraphService graphService,
        ILogger log)
    {
        log.LogError(
            "Email {MessageId} opgegeven na {Pogingen} mislukte pogingen ({Fase}) — als gelezen gemarkeerd "
            + "zodat de wachtrij niet blokkeert. Handmatige opvolging nodig via het email-log.",
            email.MessageId, pogingen, fase);

        try
        {
            await SqlEmailPersistenceRepository.UpdateFoutAsync(cs, verwerkingId,
                $"Opgegeven na {pogingen} mislukte pogingen ({fase}) — handmatige opvolging nodig");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon opgeven-status niet vastleggen voor verwerking {Id}", verwerkingId);
        }

        await graphService.MarkAsReadAsync(email.MessageId);
    }

    private static async Task LegVerwerkingsFoutVastAsync(
        string cs,
        InkomendBericht email,
        string foutMelding,
        ILogger log)
    {
        try
        {
            var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(cs, email.MessageId);
            if (stand is null)
            {
                log.LogWarning(
                    "Email {MessageId}: verwerking mislukt vóórdat er een rij bestond — geen foutstatus vastgelegd",
                    email.MessageId);
                return;
            }

            await SqlEmailPersistenceRepository.UpdateFoutAsync(cs, stand.VerwerkingId, foutMelding);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon foutstatus niet vastleggen voor {MessageId}", email.MessageId);
        }
    }

    private static async Task LegVoorBeoordelingNaVerzendPogingAsync(
        string cs,
        InkomendBericht email,
        int verwerkingId,
        IEmailGraphService graphService,
        ILogger log)
    {
        log.LogWarning(
            "Email {MessageId}: er staat een verzendpoging vastgelegd zonder bekende uitkomst voor verwerking "
            + "{Id} — waarschijnlijk is een eerdere invocatie afgebroken tussen versturen en vastleggen. "
            + "Er wordt NIET opnieuw verstuurd; status op Review voor beoordeling via het email-log.",
            email.MessageId, verwerkingId);

        try
        {
            await SqlEmailPersistenceRepository.UpdateStatusAsync(cs, verwerkingId, EmailStatus.Review, null);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon Review-status niet vastleggen voor verwerking {Id}", verwerkingId);
        }

        try
        {
            await graphService.EnsureMasterCategoryAsync(EmailCategorieLabels.GeenAiAntwoord, EmailCategorieLabels.GeenAiAntwoordKleur);
            await graphService.SetCategoriesAsync(email.MessageId, EmailCategorieLabels.GeenAiAntwoord);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Outlook-labeling mislukt voor verwerking {Id}", verwerkingId);
        }

        await graphService.MarkAsReadAsync(email.MessageId);
    }

    internal static async Task RegistreerClassificatieFoutAsync(
        string cs,
        string clubCode,
        InkomendBericht email,
        IEmailGraphService graphService,
        ILogger log)
    {
        try
        {
            var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(cs, email.MessageId);
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
                        cs, email, stand!.VerwerkingId, stand.Pogingen, "AI-classificatie", graphService, log);
                    return;

                case VerwerkingsBesluit.OnbeslistNaVerzendPoging:
                    await LegVoorBeoordelingNaVerzendPogingAsync(
                        cs, email, stand!.VerwerkingId, graphService, log);
                    return;

                case VerwerkingsBesluit.HerhaalVerwerking:
                    await SqlEmailPersistenceRepository.VerhoogPogingenAsync(cs, stand!.VerwerkingId);
                    verwerkingId = stand.VerwerkingId;
                    break;

                default:
                    verwerkingId = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(cs, email, clubCode);
                    break;
            }

            await SqlEmailPersistenceRepository.UpdateFoutAsync(cs, verwerkingId, "AI-classificatie mislukt");
            log.LogWarning(
                "Email {MessageId}: AI-classificatie mislukt — poging {Poging} van {Max} vastgelegd, bericht blijft ongelezen voor de volgende poll",
                email.MessageId, (stand?.Pogingen ?? 0) + 1, EmailIdempotentie.MaxPogingen);
        }
        catch (DubbeleMessageIdException)
        {
            log.LogInformation(
                "Email {MessageId}: al door een andere verwerking geregistreerd — classificatiefout niet vastgelegd",
                email.MessageId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon mislukte AI-classificatie van {MessageId} niet vastleggen", email.MessageId);
        }
    }

    /// <summary>
    /// Teamleider-notificatie bij een herplanverzoek (#66). <b>Vereenvoudigd t.o.v. het SQL
    /// Server-origineel (zie klassekop, item 4):</b> daar komt naam+email uit
    /// <c>PlannerDataAccess.GetTeamleiderContactAsync</c>, dat op deze tier niet bestaat. In plaats
    /// daarvan wordt het e-mailadres opgezocht via de al bestaande, geteste
    /// <see cref="AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync"/> — dat levert alleen een
    /// adres, geen naam, dus de aanhef is hier generiek.
    /// </summary>
    private static async Task StuurTeamleiderNotificatieAsync(
        string cs, string clubCode, IEmailGraphService graphService, string teamNaam, string datum, ILogger log)
    {
        try
        {
            var teamleiderEmail = await AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync(cs, teamNaam, clubCode);
            if (string.IsNullOrWhiteSpace(teamleiderEmail))
            {
                log.LogInformation("Geen teamleider gevonden voor {Team} in avg.teambegeleiding — notificatie overgeslagen", teamNaam);
                return;
            }

            var plannerNaam = PostgresAppSettings.GetSetting("plannerAfzenderNaam")
                ?? throw new InvalidOperationException("Vereiste instelling 'plannerAfzenderNaam' ontbreekt in public.appsettings");

            DateOnly.TryParse(datum, out var datumDate);
            var datumDisplay = datumDate != default
                ? datumDate.ToString("dddd d MMMM yyyy", new System.Globalization.CultureInfo("nl-NL"))
                : datum;

            var notificatieBody = "Hoi,\n\n"
                + $"Er is een herplanverzoek ontvangen voor {teamNaam} op {datumDisplay}.\n\n"
                + "De coördinator heeft automatisch gereageerd op dit verzoek. "
                + "Je hoeft zelf geen actie te ondernemen, maar we willen je op de hoogte houden.\n\n"
                + "Als je vragen hebt over dit herplanverzoek, neem dan contact op met de veldplanner.\n\n"
                + $"Met vriendelijke groet,\n{plannerNaam}";

            await graphService.SendReplyAsync(
                teamleiderEmail,
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

    /// <summary>
    /// Stuurt een teambegeleiding-vraag door naar de begeleider (#168). Zie
    /// <see cref="StuurTeamleiderNotificatieAsync"/> voor dezelfde e-mailadres-lookup-afwijking.
    /// </summary>
    private static async Task StuurTeamContactBerichtDoorAsync(
        string cs, string clubCode, IEmailGraphService graphService, string teamNaam, InkomendBericht email, ILogger log)
    {
        try
        {
            var begeleiderEmail = await AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync(cs, teamNaam, clubCode);
            if (string.IsNullOrWhiteSpace(begeleiderEmail))
            {
                log.LogInformation("Geen begeleider gevonden voor {Team} — doorsturen overgeslagen", teamNaam);
                return;
            }

            // AVG-maatregel: BCC-audit-kopie naar de veldplanner bij het doorsturen van
            // persoonsgegevens. 'plannerEmailAdres' wordt vandaag niet door
            // PostgresAppSettings.LoadSettingsAsync geladen (zelfde gat als in
            // AdminTeambegeleidingFunction.Doorsturen) — vandaar de expliciete null-guard i.p.v.
            // een stille lege waarde.
            var auditKopieAdres = PostgresAppSettings.GetSetting("plannerEmailAdres");
            if (string.IsNullOrWhiteSpace(auditKopieAdres))
            {
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

            // AVG: Reply-To = email.Afzender zodat begeleider rechtstreeks kan antwoorden;
            // BCC veldplanner voor audit; begeleider-email nooit in logs.
            await graphService.StuurTeamContactDoorAsync(
                [begeleiderEmail], subject, body, email.Afzender, auditKopieAdres);

            log.LogInformation("Teambegeleiding-vraag doorgestuurd voor {Team}", teamNaam);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Fout bij doorsturen teambegeleiding-vraag voor {Team} — hoofdverwerking niet onderbroken", teamNaam);
        }
    }

    /// <summary>
    /// Reageert op een geslaagde databaseverbinding: als er een noodmail-registratie openstaat van
    /// een eerdere uitval, wordt die gewist zodat een volgende, nieuwe uitval weer een verse melding
    /// oplevert.
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
    /// voor deze uitval.
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
    /// Stuurt een noodmail als de database niet beschikbaar is. Emails blijven ongelezen in de
    /// inbox en worden bij de volgende poll opnieuw opgepikt.
    /// </summary>
    internal static async Task StuurDatabaseNoodmailAsync(
        IEmailGraphService graphService, int aantalEmails, string foutmelding,
        INoodmailThrottleStore throttleStore, ILogger log)
    {
        var mailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById(BerichtResponseGenerator.NlTijdzoneId);
        var nlTijd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nlZone);

        var body = "URGENT: De database is niet bereikbaar.\n\n"
                 + $"Tijdstip: {nlTijd:dd-MM-yyyy HH:mm}\n"
                 + $"Foutmelding: {foutmelding}\n"
                 + $"Onverwerkte emails: {aantalEmails}\n\n"
                 + "De email-processor is automatisch GEPAUZEERD. Er worden geen herhaalde meldingen verstuurd.\n"
                 + "De processor hervat automatisch zodra de database weer bereikbaar is.\n\n"
                 + "De emails blijven ongelezen in de inbox en worden automatisch verwerkt zodra de database weer beschikbaar is.\n\n"
                 + "Controleer de Postgres-hosting-omgeving (bijv. het Supabase-dashboard) op status en resource-limieten.";

        try
        {
            await graphService.SendReplyAsync(mailbox,
                "URGENT: Database niet bereikbaar — email-processor gepauzeerd", body, null);
            await throttleStore.RegistreerVerstuurdAsync(DatabaseNoodmailSleutel, DateTime.UtcNow);
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

    // Categoriseert een exception naar een privacy-safe foutomschrijving.
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

    // Sanitiseert een foutmelding voor opslag in de DB — verwijdert e-mailadressen en knipt af.
    private static string SanitizeFoutMelding(string message)
        => EmailSanitizer.SanitizeFoutMelding(message);

    /// <summary>
    /// Bepaalt of de OpenAI-quota-noodmail verstuurd mag worden, op basis van een persistente
    /// registratie in plaats van een static veld.
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

        var body = "URGENT: OpenAI quota overschreden — email-processor gepauzeerd.\n\n"
                 + $"Tijdstip: {nlTijd:dd-MM-yyyy HH:mm}\n"
                 + $"Foutmelding: {foutmelding}\n\n"
                 + "De email-processor is gestopt met de huidige batch en stuurt geen herhaalde meldingen binnen 24 uur.\n"
                 + "Onverwerkte emails blijven ongelezen in de inbox en worden opnieuw opgepikt bij de volgende poll.\n\n"
                 + "Acties:\n"
                 + "  • Controleer de OpenAI-dashboard-quota\n"
                 + "  • Verhoog de quota-limiet of wacht tot de quota vernieuwt\n"
                 + "  • Als de quota verhoogd is, hervat de processor automatisch bij de volgende poll";

        try
        {
            await graphService.SendReplyAsync(mailbox,
                "URGENT: OpenAI quota overschreden — email-processor gepauzeerd", body, null);
            await throttleStore.RegistreerVerstuurdAsync(OpenAiQuotaNoodmailSleutel, DateTime.UtcNow);
            log.LogWarning("OpenAI quota-noodmail verstuurd naar {Mailbox}", mailbox);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Kon OpenAI quota-noodmail niet versturen");
        }
    }
}
