using Planner.Shared.Email;

namespace Planner.Shared.Tests.Email.TestDoubles;

/// <summary>Een Graph-foutrespons met statuscode, voor de tier-onafhankelijke classificatie van verzendfouten (#1133).</summary>
internal sealed class GraphStatusFout(int status) : Exception($"Graph {status}")
{
    public int Status { get; } = status;
}

internal static class TestHaken
{
    /// <summary>Geen begeleidingscontact, geen bijlage; de statuscode komt uit <see cref="GraphStatusFout"/>.</summary>
    internal static ReplyTierHaken Standaard { get; } = Maak();

    internal static ReplyTierHaken Maak(
        Func<string, Task<string?>>? begeleiding = null, Func<string, Task<EmailBijlage?>>? bijlage = null)
        => new(begeleiding ?? (_ => Task.FromResult<string?>(null)),
               bijlage ?? (_ => Task.FromResult<EmailBijlage?>(null)),
               ex => ex is GraphStatusFout g ? g.Status : null);
}

internal sealed class FakeReplyGraph : IReplyGraph
{
    public List<string> MarkedAsReadIds { get; } = new();
    public List<(string MessageId, string[] Categories)> CategoryUpdates { get; } = new();
    public List<(string Name, string ColorPreset)> EnsuredCategories { get; } = new();
    public List<(string To, string Subject, string Body, string? ConversationId, IReadOnlyList<string>? Bcc, EmailBijlage? Bijlage)> SentReplies { get; } = new();

    public bool ThrowOnSendReply { get; set; }

    /// <summary>Als gezet: het bericht wordt eerst aan <see cref="SentReplies"/> toegevoegd ("Graph accepteerde het") en daarna deze fout gegooid (#1133).</summary>
    public Exception? ExceptionToThrowOnSendReply { get; set; }

    /// <summary>Hook op het moment van versturen: bewijst dat de verzendintentie er vóór staat (#716).</summary>
    public Action? OnSendReply { get; set; }

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

    public Task SendReplyAsync(string to, string subject, string body, string? conversationId,
        IReadOnlyList<string>? bcc = null, EmailBijlage? bijlage = null)
    {
        OnSendReply?.Invoke();
        if (ExceptionToThrowOnSendReply != null)
        {
            SentReplies.Add((to, subject, body, conversationId, bcc, bijlage));
            throw ExceptionToThrowOnSendReply;
        }

        if (ThrowOnSendReply)
            throw new InvalidOperationException("SendReply simulated failure");

        SentReplies.Add((to, subject, body, conversationId, bcc, bijlage));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingReplyPersistentie : IReplyPersistentie
{
    public List<(int VerwerkingId, EmailStatus Status, string? GeextraheerdeData)> StatusUpdates { get; } = new();
    public List<(int VerwerkingId, string VerstuurdNaar, string AntwoordEmail)> AntwoordUpdates { get; } = new();
    public List<(int VerwerkingId, string FoutMelding)> FoutUpdates { get; } = new();
    public List<int> VerzendPogingMarkeringen { get; } = new();
    public List<int> VerzendPogingWissingen { get; } = new();
    public List<(int VerwerkingId, string AntwoordEmail)> VoorgesteldeAntwoorden { get; } = new();

    public bool ThrowOnMarkeerVerzendPoging { get; set; }
    public bool ThrowOnUpdateAntwoordVerstuurd { get; set; }

    public Task UpdateVoorgesteldAntwoordAsync(int verwerkingId, string antwoordEmail)
    {
        VoorgesteldeAntwoorden.Add((verwerkingId, antwoordEmail));
        return Task.CompletedTask;
    }

    public Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData)
    {
        StatusUpdates.Add((verwerkingId, status, geextraheerdeData));
        return Task.CompletedTask;
    }

    public Task UpdateFoutAsync(int verwerkingId, string foutMelding)
    {
        FoutUpdates.Add((verwerkingId, foutMelding));
        return Task.CompletedTask;
    }

    public Task MarkeerVerzendPogingAsync(int verwerkingId)
    {
        if (ThrowOnMarkeerVerzendPoging)
            throw new InvalidOperationException("Vastleggen van de verzendintentie mislukt (gesimuleerd)");
        VerzendPogingMarkeringen.Add(verwerkingId);
        return Task.CompletedTask;
    }

    public Task WisVerzendPogingAsync(int verwerkingId)
    {
        VerzendPogingWissingen.Add(verwerkingId);
        return Task.CompletedTask;
    }

    public Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail)
    {
        if (ThrowOnUpdateAntwoordVerstuurd)
            throw new InvalidOperationException("Vastleggen van het antwoord mislukt (gesimuleerd)");
        AntwoordUpdates.Add((verwerkingId, verstuurdNaar, antwoordEmail));
        return Task.CompletedTask;
    }
}
