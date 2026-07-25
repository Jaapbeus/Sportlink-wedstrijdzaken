using Microsoft.Extensions.Logging;
using SportlinkFunction.Email;

namespace FunctionApp.Tests.Email.TestDoubles;

internal sealed class RecordingEmailPersistenceService : IEmailPersistenceService
{
    public List<(int VerwerkingId, EmailStatus Status, string? GeextraheerdeData)> StatusUpdates { get; } = new();

    public List<(int VerwerkingId, string VerstuurdNaar, string AntwoordEmail)> AntwoordUpdates { get; } = new();

    public List<(string MessageId, string FoutMelding)> FoutUpdates { get; } = new();

    public Task<HashSet<string>> LaadUitgeslotenAdressenAsync(ILogger log)
        => Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public Task<bool> BestaatMessageIdAsync(string messageId)
        => Task.FromResult(false);

    public Task<int> InsertEmailVerwerkingAsync(InkomendBericht email)
        => Task.FromResult(1);

    public Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData)
    {
        StatusUpdates.Add((verwerkingId, status, geextraheerdeData));
        return Task.CompletedTask;
    }

    public Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson)
        => Task.CompletedTask;

    public Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail)
    {
        AntwoordUpdates.Add((verwerkingId, verstuurdNaar, antwoordEmail));
        return Task.CompletedTask;
    }

    public Task UpdateFoutAsync(string messageId, string foutMelding)
    {
        FoutUpdates.Add((messageId, foutMelding));
        return Task.CompletedTask;
    }

    public Task<(bool IsReply, int? OrigineleVerwerkingId, string? OrigineelType, string? OriginaleSamenvatting)>
        DetecteerReplyOpOnsAntwoordAsync(string conversationId, ILogger log)
        => Task.FromResult((false, (int?)null, (string?)null, (string?)null));

    public Task UpdateReplyStatusAsync(int verwerkingId, bool isReply, int replyOpVerwerkingId)
        => Task.CompletedTask;

    public Task InsertClassificatieCorrectieAsync(
        int origineleVerwerkingId,
        int correctionVerwerkingId,
        string origineelType,
        string? afgeleidType,
        string? originaleSamenvatting,
        string? correctieSamenvatting)
        => Task.CompletedTask;

    public Task<List<ClassificatieCorrectieVoorbeeld>> HaalLeermomentVoorbeeldenOpAsync(ILogger log)
        => Task.FromResult(new List<ClassificatieCorrectieVoorbeeld>());

    public string ResolveClubCode() => "VRC";
}
