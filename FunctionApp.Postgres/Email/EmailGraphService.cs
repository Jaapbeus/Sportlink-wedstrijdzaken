using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Planner.Shared;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Het uitgaande e-mailpad van deze tier (issue 888 vervolg, §43) — vandaag uitsluitend gebruikt
/// door <c>AdminTeambegeleidingDoorsturen</c>.
///
/// <para>
/// <b>Bewust smaller dan het SQL Server-origineel.</b> Dat kent zes methoden
/// (<c>GetUnreadEmailsAsync</c>, <c>SetCategoriesAsync</c>, <c>EnsureMasterCategoryAsync</c>,
/// <c>MarkAsReadAsync</c>, <c>SendReplyAsync</c> en <c>StuurTeamContactDoorAsync</c>). De eerste
/// vijf horen bij de inkomende e-mailverwerkingspijplijn — <c>EmailProcessorFunction</c> en de
/// AI-classificatie — en die bestaat op deze tier niet. Ze hier meeporten zou onverifieerbare dode
/// code opleveren: precies wat §41 en §16 bij andere methoden ook niet gedaan hebben. Zodra die
/// pijplijn wél vertaald wordt, groeit dit contract mee.
/// </para>
/// </summary>
public interface IEmailGraphService
{
    /// <summary>
    /// Stuurt een vraag over teambegeleiding door naar de begeleider(s).
    /// <c>Reply-To</c> wijst naar de vraagsteller zodat een antwoord rechtstreeks bij hem
    /// terechtkomt; de coördinator gaat in BCC mee.
    /// </summary>
    Task StuurTeamContactDoorAsync(
        IReadOnlyList<string> ontvangers,
        string subject,
        string body,
        string? aanvragerEmail,
        string? coordinatorEmail);
}

/// <summary>
/// Graph-implementatie van <see cref="IEmailGraphService"/> — 1-op-1 dezelfde opbouw als
/// <c>FunctionApp/Email/EmailGraphService.StuurTeamContactDoorAsync</c>, inclusief de
/// AVG-discipline in de logging: wél het aantal ontvangers, nooit namen of adressen.
/// <para>
/// De HTML-sanitisatie loopt via het gedeelde <see cref="EmailSanitizer"/> (§43) — sanitisatie
/// hoort één implementatie en één testsuite te hebben, dus die is verhuisd naar
/// <c>Planner.Shared</c> in plaats van hier gekopieerd.
/// </para>
/// </summary>
public sealed class EmailGraphService : IEmailGraphService
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
                message.ReplyTo = [new Recipient { EmailAddress = new EmailAddress { Address = aanvragerEmail } }];

            if (!string.IsNullOrEmpty(coordinatorEmail))
                message.BccRecipients = [new Recipient { EmailAddress = new EmailAddress { Address = coordinatorEmail } }];

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
}
