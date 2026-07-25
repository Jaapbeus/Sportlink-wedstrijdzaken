namespace SportlinkFunction.Email;

/// <summary>
/// Abstraheert Graph-operaties voor emailverwerking.
/// </summary>
public interface IEmailGraphService
{
    Task<List<InkomendBericht>> GetUnreadEmailsAsync();
    Task SetCategoriesAsync(string messageId, params string[] categories);
    Task EnsureMasterCategoryAsync(string name, string colorPreset);
    Task MarkAsReadAsync(string messageId);
    Task SendReplyAsync(string to, string subject, string body, string? conversationId);
    Task StuurTeamContactDoorAsync(
        string coachEmail,
        string subject,
        string body,
        string? aanvragerEmail,
        string? coordinatorEmail);
}
