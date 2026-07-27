using Microsoft.Extensions.Logging;
using SportlinkFunction.Email;

namespace FunctionApp.Tests.Email.TestDoubles;

internal sealed class FakeEmailPersistenceRepository : IEmailPersistenceRepository
{
    public HashSet<string> ExcludedAddressesToReturn { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? LastExcludedClubCode { get; private set; }

    public (string ConversationId, string ClubCode)? LastDetecteerCall { get; private set; }

    public (bool IsReply, int? OrigineleVerwerkingId, string? OrigineelType, string? OriginaleSamenvatting)
        DetecteerResult { get; set; }

    public (
        int OrigineleVerwerkingId,
        int CorrectionVerwerkingId,
        string OrigineelType,
        string? AfgeleidType,
        string? OriginaleSamenvatting,
        string? CorrectieSamenvatting,
        string ClubCode)? LastCorrectieCall { get; private set; }

    public Task<HashSet<string>> GetExcludedEmailAddressesAsync(string clubCode)
    {
        LastExcludedClubCode = clubCode;
        return Task.FromResult(new HashSet<string>(ExcludedAddressesToReturn, StringComparer.OrdinalIgnoreCase));
    }

    public Task<(bool IsReply, int? OrigineleVerwerkingId, string? OrigineelType, string? OriginaleSamenvatting)>
        DetecteerReplyOpOnsAntwoordAsync(string conversationId, string clubCode, ILogger log)
    {
        LastDetecteerCall = (conversationId, clubCode);
        return Task.FromResult(DetecteerResult);
    }

    public Task InsertClassificatieCorrectieAsync(
        int origineleVerwerkingId,
        int correctionVerwerkingId,
        string origineelType,
        string? afgeleidType,
        string? originaleSamenvatting,
        string? correctieSamenvatting,
        string clubCode)
    {
        LastCorrectieCall = (
            origineleVerwerkingId,
            correctionVerwerkingId,
            origineelType,
            afgeleidType,
            originaleSamenvatting,
            correctieSamenvatting,
            clubCode);
        return Task.CompletedTask;
    }

    public string? LastStandMessageId { get; private set; }

    public EmailVerwerkingStand? StandToReturn { get; set; }

    public Task<EmailVerwerkingStand?> HaalVerwerkingStandOpAsync(string messageId)
    {
        LastStandMessageId = messageId;
        return Task.FromResult(StandToReturn);
    }

    public Task<int> InsertEmailVerwerkingAsync(InkomendBericht email) => throw new NotImplementedException();

    public Task VerhoogPogingenAsync(int verwerkingId) => throw new NotImplementedException();

    public Task UpdateVoorgesteldAntwoordAsync(int verwerkingId, string antwoordEmail) => throw new NotImplementedException();

    public Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData) => throw new NotImplementedException();

    public Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson) => throw new NotImplementedException();

    public Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail) => throw new NotImplementedException();

    public Task UpdateFoutAsync(string messageId, string foutMelding) => throw new NotImplementedException();

    public Task UpdateReplyStatusAsync(int verwerkingId, bool isReply, int replyOpVerwerkingId) => throw new NotImplementedException();

    public Task<List<ClassificatieCorrectieVoorbeeld>> HaalLeermomentVoorbeeldenOpAsync(string clubCode, ILogger log)
        => throw new NotImplementedException();
}
