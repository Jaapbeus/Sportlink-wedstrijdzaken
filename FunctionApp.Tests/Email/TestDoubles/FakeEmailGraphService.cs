using SportlinkFunction.Email;

namespace FunctionApp.Tests.Email.TestDoubles;

internal sealed class FakeEmailGraphService : IEmailGraphService
{
    public List<InkomendBericht> UnreadEmails { get; } = new();
    public List<string> MarkedAsReadIds { get; } = new();
    public List<(string MessageId, string[] Categories)> CategoryUpdates { get; } = new();
    public List<(string Name, string ColorPreset)> EnsuredCategories { get; } = new();
    public List<(string To, string Subject, string Body, string? ConversationId, IReadOnlyList<string>? Bcc, EmailBijlage? Bijlage)> SentReplies { get; } = new();
    public List<(IReadOnlyList<string> Ontvangers, string Subject, string Body, string? AanvragerEmail, string? CoordinatorEmail)> TeamForwardings { get; } = new();

    public bool ThrowOnSendReply { get; set; }

    public Task<List<InkomendBericht>> GetUnreadEmailsAsync()
        => Task.FromResult(UnreadEmails.ToList());

    public Task SetCategoriesAsync(string messageId, params string[] categories)
    {
        CategoryUpdates.Add((messageId, categories));
        return Task.CompletedTask;
    }

    public Task EnsureMasterCategoryAsync(string name, string colorPreset)
    {
        EnsuredCategories.Add((name, colorPreset));
        return Task.CompletedTask;
    }

    public Task MarkAsReadAsync(string messageId)
    {
        MarkedAsReadIds.Add(messageId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hook die precies op het moment van versturen wordt aangeroepen. Nodig om vast te leggen dát de
    /// verzendintentie al in de database staat vóórdat de mail de deur uit gaat (#716) — de volgorde is
    /// daar het hele punt, en die is niet te zien aan de eindtoestand.
    /// </summary>
    public Action? OnSendReply { get; set; }

    public Task SendReplyAsync(string to, string subject, string body, string? conversationId,
        IReadOnlyList<string>? bcc = null, EmailBijlage? bijlage = null)
    {
        OnSendReply?.Invoke();

        if (ThrowOnSendReply)
            throw new InvalidOperationException("SendReply simulated failure");

        SentReplies.Add((to, subject, body, conversationId, bcc, bijlage));
        return Task.CompletedTask;
    }

    public Task StuurTeamContactDoorAsync(
        IReadOnlyList<string> ontvangers,
        string subject,
        string body,
        string? aanvragerEmail,
        string? coordinatorEmail)
    {
        TeamForwardings.Add((ontvangers, subject, body, aanvragerEmail, coordinatorEmail));
        return Task.CompletedTask;
    }
}
