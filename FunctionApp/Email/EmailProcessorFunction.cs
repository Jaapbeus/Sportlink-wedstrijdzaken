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
    private static bool _databaseNoodmailVerstuurd;
    private static DateTime? _openAiQuotaNoodmailVerstuurdenOp;

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

        var aiService = new BerichtAiService(loggerFactory.CreateLogger<BerichtAiService>());
        var uitgeslotenAdressen = await LaadUitgeslotenAdressenAsync(log);

        int verwerkt = 0, fouten = 0;

        foreach (var email in emails)
        {
            try
            {
                await VerwerkEmailAsync(email, graphService, aiService, uitgeslotenAdressen, log);
                verwerkt++;
            }
            catch (Exception ex) when (IsOpenAiQuotaFout(ex))
            {
                log.LogError(ex, "OpenAI quota overschreden — email processor stopt voor deze batch");
                if (_openAiQuotaNoodmailVerstuurdenOp == null
                    || (DateTime.UtcNow - _openAiQuotaNoodmailVerstuurdenOp.Value).TotalHours >= 24)
                {
                    await StuurOpenAiNoodmailAsync(graphService, ex.Message, log);
                }
                else
                {
                    log.LogWarning("OpenAI quota-noodmail al verstuurd binnen 24u — geen herhaling");
                }
                break;
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
        InkomendBericht email,
        EmailGraphService graphService,
        BerichtAiService aiService,
        HashSet<string> uitgeslotenAdressen,
        ILogger log)
    {
        var eigenMailbox = Environment.GetEnvironmentVariable("GraphMailbox") ?? "";
        if (email.Afzender.Equals(eigenMailbox, StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("Email {MessageId} is van eigen mailbox, overslaan", email.MessageId);
            await graphService.MarkAsReadAsync(email.MessageId);
            return;
        }

        var internDomein = SystemUtilities.AppSettings.GetSetting("internDomein") ?? "";
        if (!string.IsNullOrWhiteSpace(internDomein)
            && email.Afzender.EndsWith("@" + internDomein.TrimStart('@'), StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("Email {MessageId} van intern domein ({Afzender}), overslaan", email.MessageId, email.Afzender);
            await graphService.MarkAsReadAsync(email.MessageId);
            return;
        }

        if (uitgeslotenAdressen.Contains(email.Afzender))
        {
            log.LogInformation("Email {MessageId} van uitgesloten adres ({Afzender}), overslaan", email.MessageId, email.Afzender);
            await graphService.MarkAsReadAsync(email.MessageId);
            return;
        }

        if (await BestaatMessageIdAsync(email.MessageId))
        {
            log.LogInformation("Email {MessageId} al verwerkt, overslaan", email.MessageId);
            await graphService.MarkAsReadAsync(email.MessageId);
            return;
        }

        var verwerkingId = await InsertEmailVerwerkingAsync(email);
        log.LogInformation("Email {MessageId} geregistreerd met id {Id}", email.MessageId, verwerkingId);

        var classificatie = await aiService.ClassificeerBerichtAsync(
            email.Body, email.Onderwerp, email.Afzender);

        BerichtPipeline.ValideerDagDatum(classificatie, email.Body, email.Onderwerp);

        var classificatieJson = JsonConvert.SerializeObject(classificatie);
        await UpdateStatusAsync(verwerkingId, EmailStatus.Geclassificeerd, classificatieJson);
        log.LogInformation("Email {Id} geclassificeerd als {Type}, datum={Datum}",
            verwerkingId, classificatie.Type, classificatie.Datum);

        if (classificatie.Type == VerzoekType.BuitenScope)
        {
            // Buiten scope: geen reply versturen, coördinator handelt zelf af.
            await UpdateStatusAsync(verwerkingId, EmailStatus.BuitenScope, null);
            await graphService.EnsureMasterCategoryAsync("Geen AI antwoord", "preset0");
            await graphService.SetCategoriesAsync(email.MessageId, "Geen AI antwoord");
            await graphService.MarkAsReadAsync(email.MessageId);
            log.LogInformation(
                "Email {Id} buiten scope — gecategoriseerd 'Geen AI antwoord', geen reply verstuurd",
                verwerkingId);
            return;
        }

        var plannerResponseJson = await BerichtPipeline.VerwerkMetPlannerAsync(classificatie, email, log);
        await UpdatePlannerResponseAsync(verwerkingId, plannerResponseJson);
        await UpdateStatusAsync(verwerkingId, EmailStatus.Verwerkt, null);

        var (onderwerp, antwoordBody) = BerichtPipeline.BouwTemplateAntwoord(
            classificatie, plannerResponseJson, email);

        var reviewMode = Environment.GetEnvironmentVariable("EmailReviewMode");
        var ontvanger = string.Equals(reviewMode, "true", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("EmailReviewRecipient") ?? email.Afzender
            : email.Afzender;

        await graphService.SendReplyAsync(ontvanger, onderwerp, antwoordBody, email.ConversationId);
        await UpdateAntwoordVerstuurdAsync(verwerkingId, ontvanger, antwoordBody);
        await graphService.MarkAsReadAsync(email.MessageId);

        log.LogInformation("Email {Id} volledig verwerkt, antwoord verstuurd naar {Ontvanger}",
            verwerkingId, ontvanger);

        // Stuur interne notificatie naar teamleider bij herplanverzoeken van externe afzender (#66)
        if (classificatie.Type == VerzoekType.HerplanVerzoek
            && !string.IsNullOrWhiteSpace(classificatie.TeamNaam)
            && !string.IsNullOrWhiteSpace(classificatie.Datum))
        {
            await StuurTeamleiderNotificatieAsync(
                graphService, classificatie.TeamNaam, classificatie.Datum, reviewMode, log);
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
                 + "  • [sql-servernaam] → myFreeDB → Overzicht → Status (moet 'Online' zijn)\n"
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
