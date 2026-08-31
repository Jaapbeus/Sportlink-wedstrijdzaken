using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.Graph.Users.Item.Outlook.MasterCategories;
using Planner.Shared;

namespace SportlinkFunction.Email;

/// <summary>
/// Wrapper rond Microsoft Graph SDK voor email-operaties via de coordinator-mailbox.
/// Ondersteunt inbox polling, emails markeren als gelezen, en antwoorden versturen.
/// </summary>
public partial class EmailGraphService : IEmailGraphService
{
    private readonly GraphServiceClient _graphClient;
    private readonly ILogger<EmailGraphService> _logger;
    private readonly string _mailbox;

    public EmailGraphService(GraphServiceClient graphClient, ILogger<EmailGraphService> logger)
    {
        _graphClient = graphClient;
        _logger = logger;
        _mailbox = Environment.GetEnvironmentVariable("GraphMailbox")
            ?? throw new InvalidOperationException("GraphMailbox environment variable is niet geconfigureerd");
    }

    /// <summary>
    /// Haalt maximaal 10 ongelezen emails op uit de inbox van de coordinator-mailbox.
    /// </summary>
    public async Task<List<InkomendBericht>> GetUnreadEmailsAsync()
    {
        var resultaat = new List<InkomendBericht>();

        try
        {
            var messages = await _graphClient.Users[_mailbox]
                .MailFolders["inbox"]
                .Messages
                .GetAsync(config =>
                {
                    config.QueryParameters.Filter = "isRead eq false";
                    config.QueryParameters.Top = 10;
                    config.QueryParameters.Orderby = ["receivedDateTime"];
                    config.QueryParameters.Select = ["id", "conversationId", "from", "subject", "receivedDateTime", "body"];
                });

            if (messages?.Value is null)
            {
                _logger.LogInformation("Geen ongelezen emails gevonden in coordinator-mailbox");
                return resultaat;
            }

            foreach (var message in messages.Value)
            {
                try
                {
                    var email = new InkomendBericht
                    {
                        MessageId = message.Id ?? "",
                        ConversationId = message.ConversationId ?? "",
                        Afzender = message.From?.EmailAddress?.Address ?? "",
                        AfzenderNaam = message.From?.EmailAddress?.Name ?? "",
                        Onderwerp = message.Subject ?? "",
                        // .UtcDateTime (niet .DateTime): dat laatste geeft Kind=Unspecified en bij een
                        // niet-nul offset de lokale tijd van die offset. Projectregel: UTC in de DB.
                        OntvangstDatum = message.ReceivedDateTime?.UtcDateTime ?? DateTime.MinValue,
                        Body = EmailSanitizer.StripHtml(message.Body?.Content ?? "")
                    };

                    resultaat.Add(email);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fout bij verwerken van email {MessageId}, wordt overgeslagen",
                        message.Id);
                }
            }

            _logger.LogInformation("{Aantal} ongelezen email(s) opgehaald uit coordinator-mailbox",
                resultaat.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij ophalen van ongelezen emails uit coordinator-mailbox");
        }

        return resultaat;
    }

    /// <summary>
    /// Zet een Outlook-categorie op een bericht. Bestaande categorieën worden vervangen.
    /// </summary>
    public async Task SetCategoriesAsync(string messageId, params string[] categories)
    {
        try
        {
            await _graphClient.Users[_mailbox]
                .Messages[messageId]
                .PatchAsync(new Message { Categories = categories.ToList() });

            _logger.LogInformation("Email {MessageId} gemarkeerd met categorie(en) {Categorieen}",
                messageId, string.Join(", ", categories));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij zetten van categorie op email {MessageId}", messageId);
        }
    }

    // Per categorienaam onthouden — met één gedeelde vlag zou de tweede categorie
    // (bijv. 'Handmatige planning', #572) nooit worden aangemaakt.
    private readonly HashSet<string> _masterCategoriesEnsured = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Zorgt dat een Outlook master-categorie bestaat met de gegeven kleur-preset.
    /// Idempotent: doet niets als de categorie al bestaat. Kleur-preset bijv. "preset0" (rood).
    /// </summary>
    public async Task EnsureMasterCategoryAsync(string name, string colorPreset)
    {
        if (_masterCategoriesEnsured.Contains(name)) return;

        try
        {
            var existing = await _graphClient.Users[_mailbox]
                .Outlook
                .MasterCategories
                .GetAsync();

            if (existing?.Value?.Any(c => string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase)) == true)
            {
                _masterCategoriesEnsured.Add(name);
                return;
            }

            await _graphClient.Users[_mailbox]
                .Outlook
                .MasterCategories
                .PostAsync(new OutlookCategory
                {
                    DisplayName = name,
                    Color = ParseCategoryColor(colorPreset)
                });

            _logger.LogInformation("Master-categorie '{Naam}' aangemaakt met kleur {Kleur}", name, colorPreset);
            _masterCategoriesEnsured.Add(name);
        }
        catch (Exception ex)
        {
            // Master-categorie aanmaken kan falen door rechten; categorie op bericht zelf werkt
            // dan nog steeds, alleen zonder gedefinieerde kleur in Outlook.
            _logger.LogWarning(ex, "Kon master-categorie '{Naam}' niet borgen — categorie op bericht werkt wel", name);
            _masterCategoriesEnsured.Add(name);
        }
    }

    private static CategoryColor ParseCategoryColor(string preset)
    {
        return Enum.TryParse<CategoryColor>(preset, ignoreCase: true, out var color)
            ? color
            : CategoryColor.Preset0;
    }

    /// <summary>
    /// Markeert een email als gelezen in de coordinator-mailbox.
    /// </summary>
    public async Task MarkAsReadAsync(string messageId)
    {
        try
        {
            await _graphClient.Users[_mailbox]
                .Messages[messageId]
                .PatchAsync(new Message { IsRead = true });

            _logger.LogInformation("Email {MessageId} gemarkeerd als gelezen", messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij markeren van email {MessageId} als gelezen", messageId);
        }
    }

    /// <summary>
    /// Verstuurt een antwoord-email via de coordinator-mailbox.
    /// </summary>
    /// <param name="bcc">
    /// Optionele BCC-adressen (#561) — bijv. de begeleiding van ons eigen team bij een
    /// verzet-zonder-datum-antwoord aan een tegenstander. Bestaande aanroepen zonder dit argument
    /// blijven ongewijzigd werken.
    /// </param>
    /// <param name="bijlage">
    /// Optionele bijlage (#561) — bijv. de KNVB-speeldagenkalender-PDF.
    /// </param>
    public async Task SendReplyAsync(string to, string subject, string body, string? conversationId,
        IReadOnlyList<string>? bcc = null, EmailBijlage? bijlage = null)
    {
        try
        {
            var message = new Message
            {
                Subject = subject,
                Body = new ItemBody
                {
                    ContentType = BodyType.Text,
                    Content = body
                },
                ToRecipients =
                [
                    new Recipient
                    {
                        EmailAddress = new EmailAddress { Address = to }
                    }
                ]
            };

            if (!string.IsNullOrEmpty(conversationId))
            {
                message.ConversationId = conversationId;
            }

            if (bcc is { Count: > 0 })
            {
                message.BccRecipients = bcc
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => new Recipient { EmailAddress = new EmailAddress { Address = a } })
                    .ToList();
            }

            if (bijlage != null)
            {
                message.Attachments =
                [
                    new FileAttachment
                    {
                        OdataType = "#microsoft.graph.fileAttachment",
                        Name = bijlage.Bestandsnaam,
                        ContentType = bijlage.ContentType,
                        ContentBytes = bijlage.Inhoud
                    }
                ];
            }

            await _graphClient.Users[_mailbox]
                .SendMail
                .PostAsync(new SendMailPostRequestBody
                {
                    Message = message
                });

            _logger.LogInformation("Antwoord verstuurd (ontvanger en onderwerp niet gelogd — AVG #210)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij versturen van antwoord (ontvanger en onderwerp niet gelogd — AVG #210)");
            throw; // aanroeper bepaalt over status — niet stil doorgaan (#432)
        }
    }

    /// <summary>
    /// Stuurt een teambegeleiding-vraag door naar één of meer ontvangers.
    /// Ontvanger-adressen blijven server-side; aanvrager ziet ze nooit (AVG art. 6.1.f).
    /// - To: ontvangers — ofwel server-side opgezocht (automatische pipeline), ofwel door een
    ///   beheerder opgegeven en gevalideerd via <see cref="SportlinkFunction.Utilities.OntvangerParser"/> (#765)
    /// - Reply-To: e-mailadres van aanvrager (Entra)
    /// - BCC: coördinator (uit AppSettings)
    ///
    /// De body gaat altijd door <see cref="EmailSanitizer.BouwVeiligeHtmlBody"/>: één van de twee
    /// aanroepers levert platte tekst met de ruwe vraag van een externe afzender erin. Ongefilterd
    /// als HTML versturen maakte van markup in die vraag een klikbare link in een mail die van de
    /// club lijkt te komen.
    /// </summary>
    public async Task StuurTeamContactDoorAsync(
        IReadOnlyList<string> ontvangers, string subject, string body,
        string? aanvragerEmail, string? coordinatorEmail)
    {
        try
        {
            var message = new Message
            {
                Subject = subject,
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = EmailSanitizer.BouwVeiligeHtmlBody(body)
                },
                ToRecipients = [.. ontvangers.Select(o => new Recipient { EmailAddress = new EmailAddress { Address = o } })]
            };

            if (!string.IsNullOrEmpty(aanvragerEmail))
            {
                message.ReplyTo = [new Recipient { EmailAddress = new EmailAddress { Address = aanvragerEmail } }];
            }

            if (!string.IsNullOrEmpty(coordinatorEmail))
            {
                message.BccRecipients = [new Recipient { EmailAddress = new EmailAddress { Address = coordinatorEmail } }];
            }

            await _graphClient.Users[_mailbox]
                .SendMail
                .PostAsync(new SendMailPostRequestBody { Message = message });

            _logger.LogInformation(
                "Teambegeleiding-vraag doorgestuurd naar {Aantal} ontvanger(s) (AVG: geen namen/emailadressen gelogd)",
                ontvangers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij doorsturen teambegeleiding-vraag (AVG: geen adressen gelogd)");
            throw;
        }
    }

    // HTML-sanitisatie (strippen bij inkomend, escapen bij uitgaand) staat in EmailSanitizer,
    // zodat beide richtingen één implementatie en één testsuite delen.
}
