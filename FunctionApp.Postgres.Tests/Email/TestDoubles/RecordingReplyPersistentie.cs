using FunctionApp.Postgres.Email;

namespace FunctionApp.Postgres.Tests.Email.TestDoubles;

/// <summary>Legt de schrijfacties van <see cref="EmailReplyPolicyService"/> vast, zonder database (#1568, M6).</summary>
internal sealed class RecordingReplyPersistentie : IReplyPersistentie
{
    public List<(int VerwerkingId, string AntwoordEmail)> VoorgesteldeAntwoorden { get; } = new();
    public List<(int VerwerkingId, EmailStatus Status)> StatusUpdates { get; } = new();
    public List<(int VerwerkingId, string FoutMelding)> FoutUpdates { get; } = new();
    public List<int> VerzendPogingMarkeringen { get; } = new();
    public List<int> VerzendPogingWissingen { get; } = new();
    public List<(int VerwerkingId, string VerstuurdNaar, string AntwoordEmail)> AntwoordUpdates { get; } = new();

    public Task UpdateVoorgesteldAntwoordAsync(int verwerkingId, string antwoordEmail)
    {
        VoorgesteldeAntwoorden.Add((verwerkingId, antwoordEmail));
        return Task.CompletedTask;
    }

    public Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData)
    {
        StatusUpdates.Add((verwerkingId, status));
        return Task.CompletedTask;
    }

    public Task UpdateFoutAsync(int verwerkingId, string foutMelding)
    {
        FoutUpdates.Add((verwerkingId, foutMelding));
        return Task.CompletedTask;
    }

    public Task MarkeerVerzendPogingAsync(int verwerkingId)
    {
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
        AntwoordUpdates.Add((verwerkingId, verstuurdNaar, antwoordEmail));
        return Task.CompletedTask;
    }
}
