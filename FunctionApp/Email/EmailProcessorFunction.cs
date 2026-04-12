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

        // 2. Services initialiseren
        var graphClient = context.InstanceServices.GetService<GraphServiceClient>();
        if (graphClient == null)
        {
            log.LogError("GraphServiceClient niet beschikbaar — controleer Graph settings");
            return;
        }

        await SystemUtilities.WaitForDatabaseAsync(log);
        await SystemUtilities.AppSettings.LoadSettingsAsync(log);

        var loggerFactory = context.InstanceServices.GetRequiredService<ILoggerFactory>();
        var graphService = new EmailGraphService(graphClient, loggerFactory.CreateLogger<EmailGraphService>());
        var aiService = new EmailAiService(loggerFactory.CreateLogger<EmailAiService>());

        // 3. Ongelezen emails ophalen
        var emails = await graphService.GetUnreadEmailsAsync();
        if (emails.Count == 0)
        {
            log.LogInformation("Geen ongelezen emails");
            return;
        }

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

        // Valideer dag-datum combinatie (bijv. "zaterdag" + woensdag-datum → corrigeer)
        ValideerDagDatum(classificatie, email.Body);

        var classificatieJson = JsonConvert.SerializeObject(classificatie);
        await UpdateStatusAsync(verwerkingId, EmailStatus.Geclassificeerd, classificatieJson);
        log.LogInformation("Email {Id} geclassificeerd als {Type}, datum={Datum}",
            verwerkingId, classificatie.Type, classificatie.Datum);

        string onderwerp;
        string antwoordBody;

        if (classificatie.Type == VerzoekType.BuitenScope)
        {
            // 4e. Buiten scope — standaard antwoord
            (onderwerp, antwoordBody) = EmailResponseGenerator.BouwBuitenScopeAntwoord(email);
            await UpdateStatusAsync(verwerkingId, EmailStatus.BuitenScope, null);
        }
        else
        {
            // 4f. Roep PlannerService aan op basis van classificatie
            var plannerResponseJson = await VerwerkMetPlannerAsync(classificatie, log);
            await UpdatePlannerResponseAsync(verwerkingId, plannerResponseJson);
            await UpdateStatusAsync(verwerkingId, EmailStatus.Verwerkt, null);

            // 4h. Bouw antwoord via templates (geen AI nodig)
            (onderwerp, antwoordBody) = BouwTemplateAntwoord(
                classificatie, plannerResponseJson, email);
        }

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
                var checkResponse = JsonConvert.DeserializeObject<CheckAvailabilityResponse>(plannerResponseJson);
                return EmailResponseGenerator.BouwBeschikbaarheidAntwoord(
                    checkResponse ?? new CheckAvailabilityResponse(), classificatie, email);

            case VerzoekType.HerplanVerzoek:
                // Herplan response is een compound object: { wedstrijd, herplanOpties }
                var herplanData = Newtonsoft.Json.Linq.JObject.Parse(plannerResponseJson);
                var wedstrijd = herplanData["wedstrijd"]?.ToObject<ZoekWedstrijdResponse>();
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
    /// Valideert dag-datum combinatie. Als de email expliciet een dag noemt
    /// ("zaterdag") maar de AI een datum retourneert die niet op die dag valt,
    /// corrigeer naar de eerstvolgende/vorige matching datum.
    /// </summary>
    private static void ValideerDagDatum(EmailClassificatie classificatie, string emailBody)
    {
        if (string.IsNullOrEmpty(classificatie.Datum)) return;
        if (!DateOnly.TryParse(classificatie.Datum, out var datum)) return;

        var dagNamen = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            ["maandag"] = DayOfWeek.Monday, ["dinsdag"] = DayOfWeek.Tuesday,
            ["woensdag"] = DayOfWeek.Wednesday, ["donderdag"] = DayOfWeek.Thursday,
            ["vrijdag"] = DayOfWeek.Friday, ["zaterdag"] = DayOfWeek.Saturday,
            ["zondag"] = DayOfWeek.Sunday
        };

        var bodyLower = emailBody.ToLowerInvariant();
        foreach (var (naam, dag) in dagNamen)
        {
            if (!bodyLower.Contains(naam)) continue;

            // Email noemt deze dag — klopt de datum?
            if (datum.DayOfWeek == dag) return; // Klopt

            // Zoek de eerstvolgende matching datum (max 7 dagen vooruit/achteruit)
            for (int offset = 1; offset <= 7; offset++)
            {
                var vooruit = datum.AddDays(offset);
                if (vooruit.DayOfWeek == dag)
                {
                    classificatie.Datum = vooruit.ToString("yyyy-MM-dd");
                    return;
                }
                var achteruit = datum.AddDays(-offset);
                if (achteruit.DayOfWeek == dag)
                {
                    classificatie.Datum = achteruit.ToString("yyyy-MM-dd");
                    return;
                }
            }
            return;
        }
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
    /// Vertaalt de AI-classificatie naar de juiste PlannerService-aanroep.
    /// </summary>
    private static async Task<string> VerwerkMetPlannerAsync(
        EmailClassificatie classificatie, ILogger log)
    {
        // Normaliseer leeftijdscategorie
        classificatie.LeeftijdsCategorie = NormaliseerLeeftijdsCategorie(classificatie.LeeftijdsCategorie);
        switch (classificatie.Type)
        {
            case VerzoekType.BeschikbaarheidCheck:
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
                            var herplanRequest = new HerplanCheckRequest
                            {
                                Wedstrijdcode = wedstrijd.Wedstrijdcode,
                                VoorkeurTijd = classificatie.AanvangsTijd
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
