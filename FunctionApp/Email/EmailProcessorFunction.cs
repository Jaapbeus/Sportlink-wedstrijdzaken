using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Newtonsoft.Json;
using SportlinkFunction.Planner;

namespace SportlinkFunction.Email;

public class EmailProcessorFunction
{
    /// <summary>
    /// Statische vlag: true zodra een noodmail is verstuurd wegens database-uitval.
    /// Voorkomt herhaalde noodmails — processor pauzeert tot de database weer bereikbaar is.
    /// Reset automatisch bij host-restart of zodra de database weer beschikbaar is.
    /// </summary>
    private static bool _databaseNoodmailVerstuurd;

    [Function("ProcessIncomingEmails")]
    public async Task Run(
        [TimerTrigger("%EMAIL_POLL_SCHEDULE%")] TimerInfo timer,
        FunctionContext context)
    {
        var log = context.GetLogger("ProcessIncomingEmails");

        // 1. Kill-switch
        if (!string.Equals(Environment.GetEnvironmentVariable("EmailProcessorEnabled"),
                "true", StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("Email processor uitgeschakeld");
            return;
        }

        // 2. Graph client initialiseren (geen database nodig)
        var graphClient = context.InstanceServices.GetService<GraphServiceClient>();
        if (graphClient == null)
        {
            log.LogError("GraphServiceClient niet beschikbaar — controleer Graph settings");
            return;
        }

        var loggerFactory = context.InstanceServices.GetRequiredService<ILoggerFactory>();
        var graphService = new EmailGraphService(graphClient, loggerFactory.CreateLogger<EmailGraphService>());

        // 3. Ongelezen emails ophalen (Graph API, wekt database NIET)
        var emails = await graphService.GetUnreadEmailsAsync();
        if (emails.Count == 0)
        {
            log.LogInformation("Geen ongelezen emails");
            return;
        }

        // 4. Pas nu database wakker maken (er zijn emails te verwerken)
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
                await StuurDatabaseNoodmailAsync(graphService, emails.Count, dbEx.Message, log);
            }
            else
            {
                log.LogWarning("Email processor gepauzeerd — database nog niet bereikbaar (noodmail al verstuurd)");
            }
            return;
        }

        var aiService = new EmailAiService(loggerFactory.CreateLogger<EmailAiService>());

        int verwerkt = 0, fouten = 0;

        // 4. Verwerk elke email
        foreach (var email in emails)
        {
            try
            {
                await VerwerkEmailAsync(email, graphService, aiService, log);
                verwerkt++;
            }
            catch (Exception ex)
            {
                fouten++;
                log.LogError(ex, "Fout bij verwerken van email {MessageId}: {Onderwerp}",
                    email.MessageId, email.Onderwerp);
                try { await UpdateFoutAsync(email.MessageId, ex.Message); }
                catch { /* fout bij fout-logging mag niet cascaderen */ }
            }
        }

        log.LogInformation("Email verwerking afgerond: {Verwerkt} verwerkt, {Fouten} fouten",
            verwerkt, fouten);
    }

    private static async Task VerwerkEmailAsync(
        InkomendEmail email,
        EmailGraphService graphService,
        EmailAiService aiService,
        ILogger log)
    {
        // 4a. Skip eigen emails (voorkom loop)
        var eigenMailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";
        if (email.Afzender.Equals(eigenMailbox, StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("Email {MessageId} is van eigen mailbox, overslaan", email.MessageId);
            await graphService.MarkAsReadAsync(email.MessageId);
            return;
        }

        // 4b. Deduplicatie
        if (await BestaatMessageIdAsync(email.MessageId))
        {
            log.LogInformation("Email {MessageId} al verwerkt, overslaan", email.MessageId);
            await graphService.MarkAsReadAsync(email.MessageId);
            return;
        }

        // 4b. INSERT in EmailVerwerking (Status = Ontvangen)
        var verwerkingId = await InsertEmailVerwerkingAsync(email);
        log.LogInformation("Email {MessageId} geregistreerd met id {Id}", email.MessageId, verwerkingId);

        // 4c. Classificeer met AI
        var classificatie = await aiService.ClassificeerEmailAsync(
            email.Body, email.Onderwerp, email.Afzender);

        // Valideer dag-datum: extraheer uit onderwerp (prioriteit) en body, corrigeer AI-fouten
        ValideerDagDatum(classificatie, email.Body, email.Onderwerp);

        var classificatieJson = JsonConvert.SerializeObject(classificatie);
        await UpdateStatusAsync(verwerkingId, EmailStatus.Geclassificeerd, classificatieJson);
        log.LogInformation("Email {Id} geclassificeerd als {Type}, datum={Datum}",
            verwerkingId, classificatie.Type, classificatie.Datum);

        if (classificatie.Type == VerzoekType.BuitenScope)
        {
            // 4e. Buiten scope — geen AI-antwoord versturen; coördinator handelt zelf af.
            // Markeer met categorie 'Geen AI antwoord' (rood) en zet op gelezen.
            await UpdateStatusAsync(verwerkingId, EmailStatus.BuitenScope, null);
            await graphService.EnsureMasterCategoryAsync("Geen AI antwoord", "preset0");
            await graphService.SetCategoriesAsync(email.MessageId, "Geen AI antwoord");
            await graphService.MarkAsReadAsync(email.MessageId);
            log.LogInformation(
                "Email {Id} buiten scope — gecategoriseerd 'Geen AI antwoord', geen reply verstuurd",
                verwerkingId);
            return;
        }

        // 4f. Roep PlannerService aan op basis van classificatie
        var plannerResponseJson = await VerwerkMetPlannerAsync(classificatie, email, log);
        await UpdatePlannerResponseAsync(verwerkingId, plannerResponseJson);
        await UpdateStatusAsync(verwerkingId, EmailStatus.Verwerkt, null);

        // 4h. Bouw antwoord via templates (geen AI nodig)
        var (onderwerp, antwoordBody) = BouwTemplateAntwoord(
            classificatie, plannerResponseJson, email);

        // 4j. Bepaal ontvanger (review mode vs productie)
        var reviewMode = Environment.GetEnvironmentVariable("EmailReviewMode");
        var ontvanger = string.Equals(reviewMode, "true", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("EmailReviewRecipient") ?? email.Afzender
            : email.Afzender;

        // 4k. Verstuur antwoord
        await graphService.SendReplyAsync(ontvanger, onderwerp, antwoordBody, email.ConversationId);

        // 4l. Update status
        await UpdateAntwoordVerstuurdAsync(verwerkingId, ontvanger, antwoordBody);

        // 4m. Markeer als gelezen
        await graphService.MarkAsReadAsync(email.MessageId);

        log.LogInformation("Email {Id} volledig verwerkt, antwoord verstuurd naar {Ontvanger}",
            verwerkingId, ontvanger);
    }

    /// <summary>
    /// Bouwt het antwoord via templates op basis van het classificatietype en PlannerService response.
    /// </summary>
    private static (string onderwerp, string body) BouwTemplateAntwoord(
        EmailClassificatie classificatie,
        string plannerResponseJson,
        InkomendEmail email)
    {
        switch (classificatie.Type)
        {
            case VerzoekType.BeschikbaarheidCheck:
                // Check of het een multi-datum response is
                var jobj = Newtonsoft.Json.Linq.JObject.Parse(plannerResponseJson);
                if (jobj["multiDatum"]?.ToObject<bool>() == true)
                {
                    var resultaten = new List<(string datum, CheckAvailabilityResponse response)>();
                    foreach (var item in jobj["resultaten"]!)
                    {
                        var datum = item["datum"]?.ToString() ?? "";
                        var resp = item["response"]?.ToObject<CheckAvailabilityResponse>() ?? new CheckAvailabilityResponse();
                        resultaten.Add((datum, resp));
                    }
                    return EmailResponseGenerator.BouwMultiDatumBeschikbaarheidAntwoord(
                        resultaten, classificatie, email);
                }
                var checkResponse = JsonConvert.DeserializeObject<CheckAvailabilityResponse>(plannerResponseJson);
                return EmailResponseGenerator.BouwBeschikbaarheidAntwoord(
                    checkResponse ?? new CheckAvailabilityResponse(), classificatie, email);

            case VerzoekType.HerplanVerzoek:
                var herplanData = Newtonsoft.Json.Linq.JObject.Parse(plannerResponseJson);
                var wedstrijd = herplanData["wedstrijd"]?.ToObject<ZoekWedstrijdResponse>();

                // Gewenste datum modus: beschikbaarheid op nieuwe datum
                if (herplanData["gewensteDatum"] != null && herplanData["beschikbaarheid"] != null)
                {
                    var gewensteDatum = herplanData["gewensteDatum"]?.ToString();
                    var beschikbaarheid = herplanData["beschikbaarheid"]?.ToObject<CheckAvailabilityResponse>();
                    return EmailResponseGenerator.BouwHerplanGewensteDatumAntwoord(
                        wedstrijd, gewensteDatum, beschikbaarheid, classificatie, email);
                }

                // Alternatieven op huidige dag
                var herplanOpties = herplanData["herplanOpties"]?.ToObject<HerplanCheckResponse>();
                return EmailResponseGenerator.BouwHerplanAntwoord(
                    wedstrijd, herplanOpties, classificatie, email);

            case VerzoekType.Bevestiging:
                return EmailResponseGenerator.BouwBevestigingAntwoord(email, classificatie);

            default:
                return EmailResponseGenerator.BouwBuitenScopeAntwoord(email);
        }
    }

    /// <summary>
    /// Extraheert datums uit onderwerp en body, en corrigeert de AI-classificatie.
    /// Prioriteit: expliciete datum in onderwerp > expliciete datum in body > AI datum + dag-validatie.
    /// </summary>
    private static void ValideerDagDatum(EmailClassificatie classificatie, string emailBody, string onderwerp)
    {
        // Stap 1: Zoek expliciete datum in onderwerp (bijv. "18-4-2026", "18-04-2026", "9 mei")
        var onderwerpDatum = ExtractExpliciteDatum(onderwerp);
        if (onderwerpDatum.HasValue)
        {
            classificatie.Datum = onderwerpDatum.Value.ToString("yyyy-MM-dd");
            return;
        }

        // Stap 2: Zoek expliciete datum in body (eerste dd-mm-yyyy patroon)
        var bodyDatum = ExtractExpliciteDatum(emailBody);
        if (bodyDatum.HasValue && string.IsNullOrEmpty(classificatie.Datum))
        {
            classificatie.Datum = bodyDatum.Value.ToString("yyyy-MM-dd");
            return;
        }

        // Stap 3: Dag-naam validatie als fallback
        if (string.IsNullOrEmpty(classificatie.Datum)) return;
        if (!DateOnly.TryParse(classificatie.Datum, out var datum)) return;

        var tekst = (onderwerp + " " + emailBody).ToLowerInvariant();
        var dagNamen = new (string naam, DayOfWeek dag)[]
        {
            ("maandag", DayOfWeek.Monday), ("dinsdag", DayOfWeek.Tuesday),
            ("woensdag", DayOfWeek.Wednesday), ("donderdag", DayOfWeek.Thursday),
            ("vrijdag", DayOfWeek.Friday), ("zaterdag", DayOfWeek.Saturday),
            ("zondag", DayOfWeek.Sunday)
        };

        foreach (var (naam, dag) in dagNamen)
        {
            if (!tekst.Contains(naam)) continue;
            if (datum.DayOfWeek == dag) return;

            for (int offset = 1; offset <= 7; offset++)
            {
                if (datum.AddDays(-offset).DayOfWeek == dag)
                    { classificatie.Datum = datum.AddDays(-offset).ToString("yyyy-MM-dd"); return; }
                if (datum.AddDays(offset).DayOfWeek == dag)
                    { classificatie.Datum = datum.AddDays(offset).ToString("yyyy-MM-dd"); return; }
            }
            return;
        }
    }

    /// <summary>
    /// Extraheert een expliciete datum uit tekst. Herkent patronen als "18-4-2026", "18-04-2026", "9 mei 2026", "9 mei".
    /// </summary>
    private static DateOnly? ExtractExpliciteDatum(string tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst)) return null;

        // Patroon: dd-mm-yyyy of d-m-yyyy
        var numericMatch = System.Text.RegularExpressions.Regex.Match(tekst, @"(\d{1,2})-(\d{1,2})-(\d{4})");
        if (numericMatch.Success)
        {
            if (int.TryParse(numericMatch.Groups[1].Value, out var dag) &&
                int.TryParse(numericMatch.Groups[2].Value, out var maand) &&
                int.TryParse(numericMatch.Groups[3].Value, out var jaar))
            {
                try { return new DateOnly(jaar, maand, dag); } catch { }
            }
        }

        // Patroon: "d maand" of "d maand yyyy" (bijv. "9 mei", "25 april 2026")
        var maandNamen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["januari"] = 1, ["februari"] = 2, ["maart"] = 3, ["april"] = 4,
            ["mei"] = 5, ["juni"] = 6, ["juli"] = 7, ["augustus"] = 8,
            ["september"] = 9, ["oktober"] = 10, ["november"] = 11, ["december"] = 12
        };

        var tekstLower = tekst.ToLowerInvariant();
        foreach (var (naam, maandNr) in maandNamen)
        {
            var maandMatch = System.Text.RegularExpressions.Regex.Match(tekstLower, $@"(\d{{1,2}})\s+{naam}(?:\s+(\d{{4}}))?");
            if (maandMatch.Success && int.TryParse(maandMatch.Groups[1].Value, out var d))
            {
                var j = maandMatch.Groups[2].Success && int.TryParse(maandMatch.Groups[2].Value, out var jj)
                    ? jj : DateTime.Now.Year;
                try { return new DateOnly(j, maandNr, d); } catch { }
            }
        }

        return null;
    }

    /// <summary>
    /// Normaliseert teamnaam: "O13-1" → "VRC JO13-1", "JO14-3" → "VRC JO14-3", etc.
    /// </summary>
    private static string? NormaliseerTeamNaam(string? teamNaam)
    {
        if (string.IsNullOrWhiteSpace(teamNaam)) return teamNaam;
        var t = teamNaam.Trim();

        // Slash tussen cijfers → streep (bijv. "JO17/3" → "JO17-3", "17/1" → "17-1").
        // Trainers schrijven vaak "jo17/3" terwijl Sportlink "JO17-3" gebruikt; zonder
        // deze normalisatie faalt de LIKE-zoekopdracht in FindMatchAsync.
        t = System.Text.RegularExpressions.Regex.Replace(t, @"(\d)\s*/\s*(\d)", "$1-$2");

        // "Onder 13-1" → "JO13-1"
        if (t.StartsWith("Onder ", StringComparison.OrdinalIgnoreCase))
            t = "JO" + t[6..].Trim();

        // "O13-1" → "JO13-1" (maar niet "MO13-1")
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^O\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !t.StartsWith("MO", StringComparison.OrdinalIgnoreCase))
            t = "J" + t.ToUpper();

        // Alleen "VRC " prefix toevoegen als de naam er uitziet als een eigen team
        // (start met JO/MO/VR/JM/V + cijfer, of is puur cijfer/letters zonder clubnaam).
        // Namen met spatie erin zijn meestal externe clubs (bijv. "VOP JO14-1", "Hooglanderveen JO16-4").
        bool looksLikeVrcTeam = System.Text.RegularExpressions.Regex.IsMatch(t, @"^(JO|MO|VR|JM|ZO)\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                              || !t.Contains(' ');
        if (looksLikeVrcTeam && !t.StartsWith("VRC ", StringComparison.OrdinalIgnoreCase))
            t = "VRC " + t;

        return t;
    }

    /// <summary>
    /// Normaliseert leeftijdscategorie: O13 → JO13, Onder 11 → JO11, etc.
    /// </summary>
    private static string? NormaliseerLeeftijdsCategorie(string? categorie)
    {
        if (string.IsNullOrWhiteSpace(categorie)) return categorie;
        var c = categorie.Trim();
        // "Onder 13" → "JO13"
        if (c.StartsWith("Onder ", StringComparison.OrdinalIgnoreCase))
            c = "JO" + c[6..].Trim();
        // "O13" → "JO13" (maar niet "MO13")
        if (System.Text.RegularExpressions.Regex.IsMatch(c, @"^O\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !c.StartsWith("MO", StringComparison.OrdinalIgnoreCase))
            c = "J" + c.ToUpper();
        return c;
    }

    /// <summary>
    /// Bepaalt of een herplanverzoek vervroeging of verlating betreft, op basis van trefwoorden
    /// in onderwerp en body. Geeft null terug als beide of geen van beide voorkomen — dan vallen
    /// we terug op het standaardgedrag (alle slots, gesorteerd op nabijheid).
    /// </summary>
    private static string? DetecteerRichting(string onderwerp, string body)
    {
        var tekst = ((onderwerp ?? "") + " " + (body ?? "")).ToLowerInvariant();
        bool vervroegen = tekst.Contains("vervroeg") || tekst.Contains("eerder")
                       || tekst.Contains("naar voren");
        bool verlaten = tekst.Contains("verlaat") || tekst.Contains("verlat")
                     || tekst.Contains(" later") || tekst.Contains("naar achter");
        if (vervroegen && !verlaten) return "vervroegen";
        if (verlaten && !vervroegen) return "verlaten";
        return null;
    }

    /// <summary>
    /// Vertaalt de AI-classificatie naar de juiste PlannerService-aanroep.
    /// </summary>
    private static async Task<string> VerwerkMetPlannerAsync(
        EmailClassificatie classificatie, InkomendEmail email, ILogger log)
    {
        // Normaliseer leeftijdscategorie en teamnaam
        classificatie.LeeftijdsCategorie = NormaliseerLeeftijdsCategorie(classificatie.LeeftijdsCategorie);

        // Bepaal welke naam het VRC-team is (kan in TeamNaam of Tegenstander staan)
        var team = classificatie.TeamNaam ?? "";
        var tegenstander = classificatie.Tegenstander ?? "";

        // Heuristiek: naam zonder clubnaam erin is waarschijnlijk het VRC-team
        // Externe teams bevatten vaak de clubnaam (bijv. "Woudenberg MO15-1", "Hooglanderveen JO16-4")
        // Swap alleen als BEIDE velden gevuld zijn en de rollen duidelijk omgedraaid zijn
        if (!string.IsNullOrWhiteSpace(team) && !string.IsNullOrWhiteSpace(tegenstander))
        {
            bool teamIsVrc = !team.Contains(' ') || team.StartsWith("VRC", StringComparison.OrdinalIgnoreCase);
            bool tegenstanderIsVrc = !tegenstander.Contains(' ') || tegenstander.StartsWith("VRC", StringComparison.OrdinalIgnoreCase);

            if (!teamIsVrc && tegenstanderIsVrc)
            {
                // Tegenstander veld bevat het VRC-team → swap
                classificatie.TeamNaam = tegenstander;
                classificatie.Tegenstander = team;
            }
        }

        classificatie.TeamNaam = NormaliseerTeamNaam(classificatie.TeamNaam);

        switch (classificatie.Type)
        {
            case VerzoekType.BeschikbaarheidCheck:
                var alleDatums = classificatie.GetAlleDatums();
                if (alleDatums.Count > 1)
                {
                    // Multi-datum: check beschikbaarheid per datum
                    var multiResults = new List<object>();
                    foreach (var datum in alleDatums)
                    {
                        var req = new CheckAvailabilityRequest
                        {
                            Datum = datum,
                            AanvangsTijd = classificatie.AanvangsTijd,
                            LeeftijdsCategorie = classificatie.LeeftijdsCategorie,
                            TeamNaam = classificatie.TeamNaam,
                            Tegenstander = classificatie.Tegenstander
                        };
                        var resp = await PlannerService.CheckAvailabilityAsync(req, log);
                        multiResults.Add(new { datum, response = resp });
                    }
                    return JsonConvert.SerializeObject(new { multiDatum = true, resultaten = multiResults });
                }
                var checkRequest = new CheckAvailabilityRequest
                {
                    Datum = classificatie.Datum ?? "",
                    AanvangsTijd = classificatie.AanvangsTijd,
                    LeeftijdsCategorie = classificatie.LeeftijdsCategorie,
                    TeamNaam = classificatie.TeamNaam,
                    Tegenstander = classificatie.Tegenstander
                };
                var checkResponse = await PlannerService.CheckAvailabilityAsync(checkRequest, log);
                return JsonConvert.SerializeObject(checkResponse);

            case VerzoekType.HerplanVerzoek:
                if (!string.IsNullOrEmpty(classificatie.TeamNaam) && !string.IsNullOrEmpty(classificatie.Datum))
                {
                    if (DateOnly.TryParse(classificatie.Datum, out var datum))
                    {
                        var wedstrijd = await PlannerDataAccess.FindMatchAsync(classificatie.TeamNaam, datum);
                        if (wedstrijd != null)
                        {
                            // Als er een gewenste datum is, check beschikbaarheid op DIE datum
                            if (!string.IsNullOrEmpty(classificatie.GewensteDatum))
                            {
                                var gewenstRequest = new CheckAvailabilityRequest
                                {
                                    Datum = classificatie.GewensteDatum,
                                    LeeftijdsCategorie = classificatie.LeeftijdsCategorie,
                                    TeamNaam = classificatie.TeamNaam
                                };
                                var beschikbaarheid = await PlannerService.CheckAvailabilityAsync(gewenstRequest, log);
                                return JsonConvert.SerializeObject(new { wedstrijd, gewensteDatum = classificatie.GewensteDatum, beschikbaarheid });
                            }

                            // Geen gewenste datum → check alternatieven op huidige dag.
                            // Richtingdetectie op trefwoorden in onderwerp+body: trainers schrijven
                            // expliciet "vervroegen"/"eerder" of "later"/"verlaten"; bij beide of
                            // geen van beide blijft Richting null (default-gedrag, voor- én nakijken).
                            var herplanRequest = new HerplanCheckRequest
                            {
                                Wedstrijdcode = wedstrijd.Wedstrijdcode,
                                VoorkeurTijd = classificatie.AanvangsTijd,
                                Richting = DetecteerRichting(email.Onderwerp, email.Body)
                            };
                            var herplanResponse = await PlannerService.CheckRescheduleAvailabilityAsync(herplanRequest, log);
                            return JsonConvert.SerializeObject(new { wedstrijd, herplanOpties = herplanResponse });
                        }
                        return JsonConvert.SerializeObject(new { gevonden = false, reden = $"Geen wedstrijd gevonden voor {classificatie.TeamNaam} op {classificatie.Datum}" });
                    }
                }
                return JsonConvert.SerializeObject(new { error = "Onvoldoende gegevens voor herplanverzoek (team en datum nodig)" });

            case VerzoekType.Bevestiging:
                return JsonConvert.SerializeObject(new { status = "Bevestiging ontvangen", opmerking = "Bevestigingen vereisen handmatige afhandeling door de coördinator" });

            default:
                return JsonConvert.SerializeObject(new { status = "Niet verwerkt" });
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
                 + "Mogelijke oorzaak: Azure SQL free tier maandlimiet bereikt.\n"
                 + "Actie: Azure Portal → SQL database → Compute and Storage → \"Continue using database with additional charges\"";

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

    // --- Database operaties ---

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

    private static async Task<int> InsertEmailVerwerkingAsync(InkomendEmail email)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(@"
            INSERT INTO [planner].[EmailVerwerking]
                ([MessageId], [ConversationId], [Afzender], [Onderwerp], [OntvangstDatum], [EmailBody], [VerzoekType], [Status])
            VALUES
                (@MessageId, @ConversationId, @Afzender, @Onderwerp, @OntvangstDatum, @EmailBody, 'Onbekend', 'Ontvangen');
            SELECT CAST(SCOPE_IDENTITY() AS INT);", connection);

        command.Parameters.AddWithValue("@MessageId", email.MessageId);
        command.Parameters.AddWithValue("@ConversationId", (object?)email.ConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@Afzender", email.Afzender);
        command.Parameters.AddWithValue("@Onderwerp", email.Onderwerp);
        command.Parameters.AddWithValue("@OntvangstDatum", email.OntvangstDatum);
        command.Parameters.AddWithValue("@EmailBody", (object?)email.Body ?? DBNull.Value);

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();

        var setClauses = "[Status] = @Status, [mta_modified] = GETDATE()";
        if (geextraheerdeData != null)
            setClauses += ", [GeextraheerdeData] = @Data, [VerzoekType] = @VerzoekType";

        using var command = new SqlCommand(
            $"UPDATE [planner].[EmailVerwerking] SET {setClauses} WHERE [Id] = @Id", connection);
        command.Parameters.AddWithValue("@Id", verwerkingId);
        command.Parameters.AddWithValue("@Status", status.ToString());

        if (geextraheerdeData != null)
        {
            command.Parameters.AddWithValue("@Data", geextraheerdeData);
            // Extraheer VerzoekType uit de classificatie JSON
            try
            {
                var classificatie = JsonConvert.DeserializeObject<EmailClassificatie>(geextraheerdeData);
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
            SET [PlannerResponse] = @Response, [mta_modified] = GETDATE()
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
            SET [Status] = 'AntwoordVerstuurd', [VerstuurdNaar] = @Naar, [AntwoordEmail] = @Antwoord, [mta_modified] = GETDATE()
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
            SET [Status] = 'Fout', [FoutMelding] = @Fout, [mta_modified] = GETDATE()
            WHERE [MessageId] = @MessageId", connection);
        command.Parameters.AddWithValue("@MessageId", messageId);
        command.Parameters.AddWithValue("@Fout", foutMelding.Length > 1000 ? foutMelding[..1000] : foutMelding);
        await command.ExecuteNonQueryAsync();
    }
}
