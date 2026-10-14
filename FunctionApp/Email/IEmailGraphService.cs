using Planner.Shared.Email;

namespace SportlinkFunction.Email;

/// <summary>
/// Abstraheert Graph-operaties voor emailverwerking.
/// </summary>
public interface IEmailGraphService : IReplyGraph
{
    Task<List<InkomendBericht>> GetUnreadEmailsAsync();
    Task StuurTeamContactDoorAsync(
        IReadOnlyList<string> ontvangers,
        string subject,
        string body,
        string? aanvragerEmail,
        string? coordinatorEmail);
}
