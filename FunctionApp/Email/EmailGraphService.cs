using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.Graph.Users.Item.Outlook.MasterCategories;

namespace SportlinkFunction.Email;

/// <summary>
/// Wrapper rond Microsoft Graph SDK voor email-operaties via de coordinator-mailbox.
/// Ondersteunt inbox polling, emails markeren als gelezen, en antwoorden versturen.
/// </summary>
public partial class EmailGraphService
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
    public async Task<List<InkomendEmail>> GetUnreadEmailsAsync()
    {
        var resultaat = new List<InkomendEmail>();

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
                _logger.LogInformation("Geen ongelezen emails gevonden in {Mailbox}", _mailbox);
                return resultaat;
            }

            foreach (var message in messages.Value)
            {
                try
                {
                    var email = new InkomendEmail
                    {
                        MessageId = message.Id ?? "",
                        ConversationId = message.ConversationId ?? "",
                        Afzender = message.From?.EmailAddress?.Address ?? "",
                        AfzenderNaam = message.From?.EmailAddress?.Name ?? "",
                        Onderwerp = message.Subject ?? "",
                        OntvangstDatum = message.ReceivedDateTime?.DateTime ?? DateTime.MinValue,
                        Body = StripHtml(message.Body?.Content ?? "")
                    };

                    resultaat.Add(email);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fout bij verwerken van email {MessageId}, wordt overgeslagen",
                        message.Id);
                }
            }

            _logger.LogInformation("{Aantal} ongelezen email(s) opgehaald uit {Mailbox}",
                resultaat.Count, _mailbox);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij ophalen van ongelezen emails uit {Mailbox}", _mailbox);
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

    private bool _masterCategoryEnsured;

    /// <summary>
    /// Zorgt dat een Outlook master-categorie bestaat met de gegeven kleur-preset.
    /// Idempotent: doet niets als de categorie al bestaat. Kleur-preset bijv. "preset0" (rood).
    /// </summary>
    public async Task EnsureMasterCategoryAsync(string name, string colorPreset)
    {
        if (_masterCategoryEnsured) return;

        try
        {
            var existing = await _graphClient.Users[_mailbox]
                .Outlook
                .MasterCategories
                .GetAsync();

            if (existing?.Value?.Any(c => string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase)) == true)
            {
                _masterCategoryEnsured = true;
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
            _masterCategoryEnsured = true;
        }
        catch (Exception ex)
        {
            // Master-categorie aanmaken kan falen door rechten; categorie op bericht zelf werkt
            // dan nog steeds, alleen zonder gedefinieerde kleur in Outlook.
            _logger.LogWarning(ex, "Kon master-categorie '{Naam}' niet borgen — categorie op bericht werkt wel", name);
            _masterCategoryEnsured = true;
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
    public async Task SendReplyAsync(string to, string subject, string body, string? conversationId)
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

            await _graphClient.Users[_mailbox]
                .SendMail
                .PostAsync(new SendMailPostRequestBody
                {
                    Message = message
                });

            _logger.LogInformation("Antwoord verstuurd naar {Ontvanger} met onderwerp '{Onderwerp}'",
                to, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij versturen van antwoord naar {Ontvanger} met onderwerp '{Onderwerp}'",
                to, subject);
        }
    }

    /// <summary>
    /// Verwijdert HTML-tags uit tekst en normaliseert whitespace.
    /// </summary>
    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        // Verwijder HTML-tags
        var tekst = HtmlTagRegex().Replace(html, " ");

        // Decodeer veelvoorkomende HTML-entiteiten
        tekst = tekst
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'");

        // Normaliseer whitespace
        tekst = WhitespaceRegex().Replace(tekst, " ");

        return tekst.Trim();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
