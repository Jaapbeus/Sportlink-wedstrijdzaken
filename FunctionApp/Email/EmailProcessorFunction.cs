using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Newtonsoft.Json;
using SportlinkFunction.Processing;

namespace SportlinkFunction.Email;

public class EmailProcessorFunction
{
    // volatile voor thread-safe reads vanuit meerdere invocaties (#382)
    private static volatile bool _databaseNoodmailVerstuurd;
    private static volatile bool _uitgeslotenCacheGeladen;
    private static DateTime? _openAiQuotaNoodmailVerstuurdenOp;
    // Uitsluitingslijst-cache: geladen vóór eerste AI-classificatie (fail-closed bij cold start). (#423)
    private static HashSet<string> _uitgeslotenCache = new(StringComparer.OrdinalIgnoreCase);

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
            _uitgeslotenCache,
            graphService,
            log);

        if (teClassificeren.Count == 0)
        {
            log.LogInformation("Alle emails gefilterd vóór AI-classificatie");
            return;
        }

        // Fail-closed: uitsluitingslijst moet geladen zijn vóór AI-classificatie. (#423)
        // Op cold start: probeer DB te wekken en lijst te laden. Lukt dat niet → return.
        if (!_uitgeslotenCacheGeladen)
        {
            log.LogInformation("Uitsluitingslijst nog niet geladen (cold start) — laden vóór AI-classificatie");
            try
            {
                await SystemUtilities.WaitForDatabaseAsync(log);
                await SystemUtilities.AppSettings.LoadSettingsAsync(log);
                _uitgeslotenCache = await persistenceService.LaadUitgeslotenAdressenAsync(log);
                _uitgeslotenCacheGeladen = true;
                // Re-filter met de nu geladen lijst — verwijder eerder doorgelaten uitgesloten adressen
                teClassificeren = batchFilterService.FilterUitgeslotenAdressen(teClassificeren, _uitgeslotenCache);
                log.LogInformation("Uitsluitingslijst geladen op cold start: {Aantal} adressen", _uitgeslotenCache.Count);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Uitsluitingslijst niet beschikbaar — AI-verwerking uitgesteld (fail-closed)");
                return;
            }
        }

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

        if (teVerwerken.Count == 0)
        {
            var aantalBuitenScope = classificaties.Count(c => c.Classificatie.Type == VerzoekType.BuitenScope);
            log.LogInformation(
                "Alle {Aantal} emails buiten scope{Afgebroken} — database blijft slapen",
                aantalBuitenScope,
                classificationResult.AiAborted ? " (AI batch vroegtijdig gestopt)" : "");
            return; // Database slaapt
        }

        // ── FASE 2: zwaar — alleen als er non-BuitenScope emails zijn ────────────────

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
                await StuurDatabaseNoodmailAsync(graphService, teVerwerken.Count, CategorizeerFout(dbEx), log);
            }
            else
            {
                log.LogWarning("Email processor gepauzeerd — database nog niet bereikbaar (noodmail al verstuurd)");
            }
            return;
        }

        // Refresh uitsluitingslijst nu DB wakker is — cache bijwerken voor volgende polls
        _uitgeslotenCache = await persistenceService.LaadUitgeslotenAdressenAsync(log);
        _uitgeslotenCacheGeladen = true;

        int verwerkt = 0, fouten = 0;

        foreach (var (email, classificatie) in teVerwerken)
        {
            try
            {
                await VerwerkEmailAsync(
                    email,
                    classificatie,
                    graphService,
                    _uitgeslotenCache,
                    aiService,
                    persistenceService,
                    replyPolicyService,
                    log);
                verwerkt++;
            }
            catch (Exception ex)
            {
                fouten++;
                log.LogError(ex, "Fout bij verwerken van email {MessageId} (onderwerp niet gelogd — AVG #210)",
                    email.MessageId);
                try { await persistenceService.UpdateFoutAsync(email.MessageId, SanitizeFoutMelding(ex.Message)); }
                catch { /* fout bij fout-logging mag niet cascaderen */ }
            }
        }

        log.LogInformation("Email verwerking afgerond: {Verwerkt} verwerkt, {Fouten} fouten",
            verwerkt, fouten);
    }

    private static async Task VerwerkEmailAsync(
        InkomendBericht email,
        BerichtClassificatie classificatie,
        IEmailGraphService graphService,
        HashSet<string> uitgeslotenAdressen,
        BerichtAiService aiService,
        IEmailPersistenceService persistenceService,
        EmailReplyPolicyService replyPolicyService,
        ILogger log)
    {
        // Hercheck met verse DB-geladen uitsluitingslijst (kan afwijken van cache)
        if (uitgeslotenAdressen.Contains(email.Afzender))
        {
            log.LogInformation("Email {MessageId} van uitgesloten adres (verse lijst), overslaan (afzender niet gelogd — AVG #210)", email.MessageId);
            await graphService.MarkAsReadAsync(email.MessageId);
            return;
        }

        if (await persistenceService.BestaatMessageIdAsync(email.MessageId))
        {
            log.LogInformation("Email {MessageId} al verwerkt, overslaan", email.MessageId);
            await graphService.MarkAsReadAsync(email.MessageId);
            return;
        }

        // DB INSERT — classificatie is al gedaan in fase 1, resultaat wordt evt. verfijnd in fase 2
        var verwerkingId = await persistenceService.InsertEmailVerwerkingAsync(email);

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
        await persistenceService.UpdateStatusAsync(verwerkingId, EmailStatus.Geclassificeerd, classificatieJson);
        log.LogInformation("Email {Id} geregistreerd als {Type}, datum={Datum}",
            verwerkingId, classificatie.Type, classificatie.Datum);

        var plannerResponseJson = await BerichtPipeline.VerwerkMetPlannerAsync(classificatie, email, log);
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

            var coordinatorEmail = SystemUtilities.AppSettings.GetSetting("coordinatorEmail");
            var subject = $"[{teamNaam}] vraag van {email.AfzenderNaam}";
            var body = $"Er is een vraag binnengekomen over de begeleiding van {teamNaam}.\n\n"
                     + $"Vraag van: {email.AfzenderNaam}\n\n"
                     + $"---\n{email.Body}\n---\n\n"
                     + "U kunt direct antwoorden op dit bericht — uw antwoord gaat naar de vraagsteller.";

            // AVG: Reply-To = email.Afzender zodat coach rechtstreeks kan antwoorden
            // BCC coördinator voor audit; coach-email nooit in logs
            await graphService.StuurTeamContactDoorAsync(
                coach.Emailadres, subject, body, email.Afzender, coordinatorEmail);

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
