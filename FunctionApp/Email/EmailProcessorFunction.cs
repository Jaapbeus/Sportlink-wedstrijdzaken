using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
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
        var graphService = new EmailGraphService(graphClient, loggerFactory.CreateLogger<EmailGraphService>());

        // ── FASE 1: licht — Graph API en AI, geen database ──────────────────────────

        var emails = await graphService.GetUnreadEmailsAsync();
        if (emails.Count == 0)
        {
            log.LogInformation("Geen ongelezen emails");
            return;
        }

        var eigenMailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";

        // Pre-filter: eigen mailbox en gecachede uitsluitingslijst (geen DB nodig)
        var teClassificeren = new List<InkomendBericht>();
        foreach (var email in emails)
        {
            if (email.Afzender.Equals(eigenMailbox, StringComparison.OrdinalIgnoreCase))
            {
                log.LogInformation("Email {MessageId} is van eigen mailbox, overslaan", email.MessageId);
                await graphService.MarkAsReadAsync(email.MessageId);
            }
            else if (_uitgeslotenCache.Contains(email.Afzender))
            {
                log.LogInformation("Email {MessageId} van uitgesloten adres (cache), overslaan (afzender niet gelogd — AVG #210)", email.MessageId);
                await graphService.MarkAsReadAsync(email.MessageId);
            }
            else
            {
                teClassificeren.Add(email);
            }
        }

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
                _uitgeslotenCache = await LaadUitgeslotenAdressenAsync(log);
                _uitgeslotenCacheGeladen = true;
                // Re-filter met de nu geladen lijst — verwijder eerder doorgelaten uitgesloten adressen
                teClassificeren = teClassificeren
                    .Where(e => !_uitgeslotenCache.Contains(e.Afzender))
                    .ToList();
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
        var classificaties = new List<(InkomendBericht Email, BerichtClassificatie Classificatie)>();
        var aiAborted = false;

        foreach (var email in teClassificeren)
        {
            try
            {
                var classificatie = await aiService.ClassificeerBerichtAsync(
                    email.Body, email.Onderwerp, email.Afzender);
                BerichtPipeline.ValideerDagDatum(classificatie, email.Body, email.Onderwerp);
                classificaties.Add((email, classificatie));
            }
            catch (Exception ex) when (IsOpenAiQuotaFout(ex))
            {
                log.LogError(ex, "OpenAI quota overschreden — email processor stopt voor deze batch");
                if (_openAiQuotaNoodmailVerstuurdenOp == null
                    || (DateTime.UtcNow - _openAiQuotaNoodmailVerstuurdenOp.Value).TotalHours >= 24)
                {
                    await StuurOpenAiNoodmailAsync(graphService, CategorizeerFout(ex), log);
                }
                else
                {
                    log.LogWarning("OpenAI quota-noodmail al verstuurd binnen 24u — geen herhaling");
                }
                aiAborted = true;
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "AI-classificatie mislukt voor email {MessageId} — blijft ongelezen voor volgende poll", email.MessageId);
            }
        }

        // BuitenScope-emails: alleen Outlook-label, database wordt niet gewekt
        foreach (var (email, _) in classificaties.Where(c => c.Classificatie.Type == VerzoekType.BuitenScope))
        {
            try
            {
                await graphService.EnsureMasterCategoryAsync("Geen AI antwoord", "preset0");
                await graphService.SetCategoriesAsync(email.MessageId, "Geen AI antwoord");
                await graphService.MarkAsReadAsync(email.MessageId);
                log.LogInformation("Email {MessageId} buiten scope — gelabeld in Outlook, database slaapt", email.MessageId);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Fout bij Outlook-labeling BuitenScope email {MessageId}", email.MessageId);
            }
        }

        var teVerwerken = classificaties
            .Where(c => c.Classificatie.Type != VerzoekType.BuitenScope)
            .ToList();

        if (teVerwerken.Count == 0)
        {
            var aantalBuitenScope = classificaties.Count(c => c.Classificatie.Type == VerzoekType.BuitenScope);
            log.LogInformation(
                "Alle {Aantal} emails buiten scope{Afgebroken} — database blijft slapen",
                aantalBuitenScope,
                aiAborted ? " (AI batch vroegtijdig gestopt)" : "");
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
        _uitgeslotenCache = await LaadUitgeslotenAdressenAsync(log);
        _uitgeslotenCacheGeladen = true;

        int verwerkt = 0, fouten = 0;

        foreach (var (email, classificatie) in teVerwerken)
        {
            try
            {
                await VerwerkEmailAsync(email, classificatie, graphService, _uitgeslotenCache, aiService, log);
                verwerkt++;
            }
            catch (Exception ex)
            {
                fouten++;
                log.LogError(ex, "Fout bij verwerken van email {MessageId} (onderwerp niet gelogd — AVG #210)",
                    email.MessageId);
                try { await UpdateFoutAsync(email.MessageId, SanitizeFoutMelding(ex.Message)); }
                catch { /* fout bij fout-logging mag niet cascaderen */ }
            }
        }

        log.LogInformation("Email verwerking afgerond: {Verwerkt} verwerkt, {Fouten} fouten",
            verwerkt, fouten);
    }

    private static async Task VerwerkEmailAsync(
        InkomendBericht email,
        BerichtClassificatie classificatie,
        EmailGraphService graphService,
        HashSet<string> uitgeslotenAdressen,
        BerichtAiService aiService,
        ILogger log)
    {
        // Hercheck met verse DB-geladen uitsluitingslijst (kan afwijken van cache)
        if (uitgeslotenAdressen.Contains(email.Afzender))
        {
            log.LogInformation("Email {MessageId} van uitgesloten adres (verse lijst), overslaan (afzender niet gelogd — AVG #210)", email.MessageId);
            await graphService.MarkAsReadAsync(email.MessageId);
            return;
        }

        if (await BestaatMessageIdAsync(email.MessageId))
        {
            log.LogInformation("Email {MessageId} al verwerkt, overslaan", email.MessageId);
            await graphService.MarkAsReadAsync(email.MessageId);
            return;
        }

        var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

        // DB INSERT — classificatie is al gedaan in fase 1, resultaat wordt evt. verfijnd in fase 2
        var verwerkingId = await InsertEmailVerwerkingAsync(email);

        // #323: reply-detectie — is dit een reply op een eerder door ons beantwoord bericht?
        if (!string.IsNullOrWhiteSpace(email.ConversationId))
        {
            var (isReply, origineleVerwerkingId, origineelType, originaleSamenvatting) =
                await DetecteerReplyOpOnsAntwoordAsync(email.ConversationId, clubCode, log);

            if (isReply && origineleVerwerkingId.HasValue)
            {
                await UpdateReplyStatusAsync(verwerkingId, true, origineleVerwerkingId.Value);
                log.LogInformation("Email {Id} is reply op verwerking {OrigineleId}", verwerkingId, origineleVerwerkingId);

                // Detecteer of het een correctie is op de eerdere classificatie
                try
                {
                    var (isCorrectie, afgeleidType, correctieSamenvatting) = await aiService.DetecteerCorrectieAsync(
                        email.Body, email.Onderwerp, origineelType ?? "", originaleSamenvatting);

                    if (isCorrectie)
                    {
                        await InsertClassificatieCorrectieAsync(
                            origineleVerwerkingId.Value, verwerkingId,
                            origineelType ?? "", afgeleidType,
                            originaleSamenvatting, correctieSamenvatting,
                            clubCode);
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
        var voorbeelden = await HaalLeermomentVoorbeeldenOpAsync(clubCode, log);
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
        await UpdateStatusAsync(verwerkingId, EmailStatus.Geclassificeerd, classificatieJson);
        log.LogInformation("Email {Id} geregistreerd als {Type}, datum={Datum}",
            verwerkingId, classificatie.Type, classificatie.Datum);

        var plannerResponseJson = await BerichtPipeline.VerwerkMetPlannerAsync(classificatie, email, log);
        await UpdatePlannerResponseAsync(verwerkingId, plannerResponseJson);
        await UpdateStatusAsync(verwerkingId, EmailStatus.Verwerkt, null);

        var (onderwerp, antwoordBody) = await BerichtPipeline.BouwTemplateAntwoord(
            classificatie, plannerResponseJson, email, log);

        var reviewMode = Environment.GetEnvironmentVariable("EmailReviewMode");
        var ontvanger = string.Equals(reviewMode, "true", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("EmailReviewRecipient") ?? email.Afzender
            : email.Afzender;

        // Fail-explicit: alleen AntwoordVerstuurd en MarkAsRead als Graph-send slaagt. (#432)
        try
        {
            await graphService.SendReplyAsync(ontvanger, onderwerp, antwoordBody, email.ConversationId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Graph-send mislukt voor verwerking {Id} — VerzendFout, mail blijft ongelezen", verwerkingId);
            try { await UpdateFoutAsync(email.MessageId, SanitizeFoutMelding(ex.Message)); } catch { }
            return; // mail NIET als gelezen markeren — wordt bij volgende poll opnieuw opgepikt
        }

        await UpdateAntwoordVerstuurdAsync(verwerkingId, ontvanger, antwoordBody);
        await graphService.MarkAsReadAsync(email.MessageId);

        log.LogInformation("Email {Id} volledig verwerkt, antwoord verstuurd (ontvanger niet gelogd — AVG #210)",
            verwerkingId);

        // Stuur interne notificatie naar teamleider bij herplanverzoeken van externe afzender (#66)
        if (classificatie.Type == VerzoekType.HerplanVerzoek
            && !string.IsNullOrWhiteSpace(classificatie.TeamNaam)
            && !string.IsNullOrWhiteSpace(classificatie.Datum))
        {
            await StuurTeamleiderNotificatieAsync(
                graphService, classificatie.TeamNaam, classificatie.Datum, reviewMode, log);
        }

        // Stuur vraag door naar coach bij team-contact-opvragen (#168)
        if (classificatie.Type == VerzoekType.TeamContactOpvragen
            && !string.IsNullOrWhiteSpace(classificatie.TeamNaam))
        {
            await StuurTeamContactBerichtDoorAsync(
                graphService, classificatie.TeamNaam, email, reviewMode, log);
        }
    }

    private static async Task StuurTeamleiderNotificatieAsync(
        EmailGraphService graphService, string teamNaam, string datum, string? reviewMode, ILogger log)
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

            var notificatieOntvanger = string.Equals(reviewMode, "true", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetEnvironmentVariable("EmailReviewRecipient") ?? teamleider.Emailadres
                : teamleider.Emailadres;

            await graphService.SendReplyAsync(
                notificatieOntvanger,
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
        EmailGraphService graphService, string teamNaam, InkomendBericht email,
        string? reviewMode, ILogger log)
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

            var coachOntvanger = string.Equals(reviewMode, "true", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetEnvironmentVariable("EmailReviewRecipient") ?? coach.Emailadres
                : coach.Emailadres;

            // AVG: Reply-To = email.Afzender zodat coach rechtstreeks kan antwoorden
            // BCC coördinator voor audit; coach-email nooit in logs
            await graphService.StuurTeamContactDoorAsync(
                coachOntvanger, subject, body, email.Afzender, coordinatorEmail);

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
        EmailGraphService graphService, int aantalEmails, string foutmelding, ILogger log)
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
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Onbekende fout";
        var gesaneerd = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
            "[e-mail]");
        return gesaneerd.Length > 200 ? gesaneerd[..200] + "…" : gesaneerd;
    }

    private static async Task StuurOpenAiNoodmailAsync(
        EmailGraphService graphService, string foutmelding, ILogger log)
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

    // --- Database operaties ---

    private static async Task<HashSet<string>> LaadUitgeslotenAdressenAsync(ILogger log)
    {
        try
        {
            var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(
                "SELECT [EmailAdres] FROM [dbo].[UitgeslotenEmailAdressen] WHERE [Actief] = 1 AND [ClubCode] = @ClubCode",
                connection);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            var adressen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                adressen.Add(reader.GetString(0));
            log.LogInformation("Uitsluitingslijst geladen: {Aantal} adressen", adressen.Count);
            return adressen;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Uitsluitingslijst kon niet worden geladen — doorgaan zonder");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static async Task<bool> BestaatMessageIdAsync(string messageId)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(
            "SELECT COUNT(1) FROM [planner].[EmailVerwerking] WHERE [MessageId] = @MessageId", connection);
        command.Parameters.AddWithValue("@MessageId", messageId);
        var count = (int)(await command.ExecuteScalarAsync())!;
        return count > 0;
    }

    private static async Task<int> InsertEmailVerwerkingAsync(InkomendBericht email)
    {
        var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(@"
            INSERT INTO [planner].[EmailVerwerking]
                ([MessageId], [ConversationId], [Afzender], [Onderwerp], [OntvangstDatum], [EmailBody], [VerzoekType], [Status], [ClubCode])
            VALUES
                (@MessageId, @ConversationId, @Afzender, @Onderwerp, @OntvangstDatum, @EmailBody, 'Onbekend', 'Ontvangen', @ClubCode);
            SELECT CAST(SCOPE_IDENTITY() AS INT);", connection);

        command.Parameters.AddWithValue("@MessageId", email.MessageId);
        command.Parameters.AddWithValue("@ConversationId", (object?)email.ConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@Afzender", email.Afzender);
        command.Parameters.AddWithValue("@Onderwerp", email.Onderwerp);
        command.Parameters.AddWithValue("@OntvangstDatum", email.OntvangstDatum);
        command.Parameters.AddWithValue("@EmailBody", (object?)email.Body ?? DBNull.Value);
        command.Parameters.AddWithValue("@ClubCode", clubCode);

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();

        var setClauses = "[Status] = @Status, [mta_modified] = GETUTCDATE()";
        if (geextraheerdeData != null)
            setClauses += ", [GeextraheerdeData] = @Data, [VerzoekType] = @VerzoekType";

        using var command = new SqlCommand(
            $"UPDATE [planner].[EmailVerwerking] SET {setClauses} WHERE [Id] = @Id", connection);
        command.Parameters.AddWithValue("@Id", verwerkingId);
        command.Parameters.AddWithValue("@Status", status.ToString());

        if (geextraheerdeData != null)
        {
            command.Parameters.AddWithValue("@Data", geextraheerdeData);
            try
            {
                var classificatie = JsonConvert.DeserializeObject<BerichtClassificatie>(geextraheerdeData);
                command.Parameters.AddWithValue("@VerzoekType", classificatie?.Type.ToString() ?? "Onbekend");
            }
            catch
            {
                command.Parameters.AddWithValue("@VerzoekType", "Onbekend");
            }
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [PlannerResponse] = @Response, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id", connection);
        command.Parameters.AddWithValue("@Id", verwerkingId);
        command.Parameters.AddWithValue("@Response", plannerResponseJson);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [Status] = 'AntwoordVerstuurd', [VerstuurdNaar] = @Naar, [AntwoordEmail] = @Antwoord, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id", connection);
        command.Parameters.AddWithValue("@Id", verwerkingId);
        command.Parameters.AddWithValue("@Naar", verstuurdNaar);
        command.Parameters.AddWithValue("@Antwoord", antwoordEmail);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdateFoutAsync(string messageId, string foutMelding)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [Status] = 'Fout', [FoutMelding] = @Fout, [mta_modified] = GETUTCDATE()
            WHERE [MessageId] = @MessageId", connection);
        command.Parameters.AddWithValue("@MessageId", messageId);
        command.Parameters.AddWithValue("@Fout", foutMelding.Length > 1000 ? foutMelding[..1000] : foutMelding);
        await command.ExecuteNonQueryAsync();
    }

    // #323: reply-detectie helpers

    private static async Task<(bool IsReply, int? OrigineleVerwerkingId, string? OrigineelType, string? OriginaleSamenvatting)>
        DetecteerReplyOpOnsAntwoordAsync(string conversationId, string clubCode, ILogger log)
    {
        try
        {
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                SELECT TOP 1 [Id], [VerzoekType], [GeextraheerdeData]
                FROM [planner].[EmailVerwerking]
                WHERE [ConversationId] = @ConversationId
                  AND [VerstuurdNaar] IS NOT NULL
                  AND [ClubCode] = @ClubCode
                ORDER BY [mta_inserted] DESC", connection);
            command.Parameters.AddWithValue("@ConversationId", conversationId);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return (false, null, null, null);

            var id = reader.GetInt32(0);
            var verzoekType = reader.IsDBNull(1) ? null : reader.GetString(1);
            var geextraheerdeData = reader.IsDBNull(2) ? null : reader.GetString(2);

            string? samenvatting = null;
            if (!string.IsNullOrEmpty(geextraheerdeData))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(geextraheerdeData);
                    if (doc.RootElement.TryGetProperty("Samenvatting", out var s))
                        samenvatting = s.GetString();
                }
                catch { /* samenvatting optioneel */ }
            }

            return (true, id, verzoekType, samenvatting);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Reply-detectie kon niet worden uitgevoerd — doorgaan als nieuw bericht");
            return (false, null, null, null);
        }
    }

    private static async Task UpdateReplyStatusAsync(int verwerkingId, bool isReply, int replyOpVerwerkingId)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [IsReplyOpOnsAntwoord] = @IsReply, [ReplyOpVerwerkingId] = @ReplyOpId, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id", connection);
        command.Parameters.AddWithValue("@Id", verwerkingId);
        command.Parameters.AddWithValue("@IsReply", isReply);
        command.Parameters.AddWithValue("@ReplyOpId", replyOpVerwerkingId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertClassificatieCorrectieAsync(
        int origineleVerwerkingId, int correctionVerwerkingId,
        string origineelType, string? afgeleidType,
        string? originaleSamenvatting, string? correctieSamenvatting,
        string clubCode)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(@"
            INSERT INTO [planner].[ClassificatieCorrectie]
                ([OrigineleVerwerkingId], [CorrectionVerwerkingId], [OrigineelVerzoekType],
                 [AfgeleidJuistType], [OrigineleSamenvatting], [CorrectieSamenvatting], [ClubCode])
            VALUES
                (@OrigineleId, @CorrectionId, @OrigineelType,
                 @AfgeleidType, @OriginaleSamenvatting, @CorrectieSamenvatting, @ClubCode)", connection);
        command.Parameters.AddWithValue("@OrigineleId", origineleVerwerkingId);
        command.Parameters.AddWithValue("@CorrectionId", correctionVerwerkingId);
        command.Parameters.AddWithValue("@OrigineelType", origineelType);
        command.Parameters.AddWithValue("@AfgeleidType", (object?)afgeleidType ?? DBNull.Value);
        command.Parameters.AddWithValue("@OriginaleSamenvatting",
            (object?)TruncateString(originaleSamenvatting, 500) ?? DBNull.Value);
        command.Parameters.AddWithValue("@CorrectieSamenvatting",
            (object?)TruncateString(correctieSamenvatting, 500) ?? DBNull.Value);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<ClassificatieCorrectieVoorbeeld>> HaalLeermomentVoorbeeldenOpAsync(
        string clubCode, ILogger log)
    {
        try
        {
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                SELECT TOP 20 [OrigineelVerzoekType], [AfgeleidJuistType],
                              [OrigineleSamenvatting], [CorrectieSamenvatting]
                FROM [planner].[ClassificatieCorrectie]
                WHERE [IsGevalideerd] = 1 AND [IsAfgewezen] = 0 AND [ClubCode] = @ClubCode
                ORDER BY [mta_modified] DESC", connection);
            command.Parameters.AddWithValue("@ClubCode", clubCode);

            var voorbeelden = new List<ClassificatieCorrectieVoorbeeld>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.IsDBNull(1)) continue;
                voorbeelden.Add(new ClassificatieCorrectieVoorbeeld(
                    OrigineelType: reader.GetString(0),
                    JuistType: reader.GetString(1),
                    OrigineleSamenvatting: reader.IsDBNull(2) ? "" : reader.GetString(2),
                    CorrectieSamenvatting: reader.IsDBNull(3) ? "" : reader.GetString(3)
                ));
            }
            return voorbeelden;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Leermomenten konden niet worden geladen — classificatie zonder few-shots");
            return new List<ClassificatieCorrectieVoorbeeld>();
        }
    }

    private static string? TruncateString(string? value, int maxLength) =>
        value == null ? null : (value.Length > maxLength ? value[..maxLength] : value);
}
