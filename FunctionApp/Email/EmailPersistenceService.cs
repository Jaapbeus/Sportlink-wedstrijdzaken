using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Email;

internal interface IEmailPersistenceService
{
    Task<HashSet<string>> LaadUitgeslotenAdressenAsync(ILogger log);
    Task<bool> BestaatMessageIdAsync(string messageId);
    Task<int> InsertEmailVerwerkingAsync(InkomendBericht email);
    Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData);
    Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson);
    Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail);
    Task UpdateFoutAsync(string messageId, string foutMelding);
    Task<(bool IsReply, int? OrigineleVerwerkingId, string? OrigineelType, string? OriginaleSamenvatting)>
        DetecteerReplyOpOnsAntwoordAsync(string conversationId, ILogger log);
    Task UpdateReplyStatusAsync(int verwerkingId, bool isReply, int replyOpVerwerkingId);
    Task InsertClassificatieCorrectieAsync(
        int origineleVerwerkingId,
        int correctionVerwerkingId,
        string origineelType,
        string? afgeleidType,
        string? originaleSamenvatting,
        string? correctieSamenvatting);
    Task<List<ClassificatieCorrectieVoorbeeld>> HaalLeermomentVoorbeeldenOpAsync(ILogger log);
    string ResolveClubCode();
}

internal sealed class EmailPersistenceService : IEmailPersistenceService
{
    private readonly IEmailPersistenceRepository _repository;
    private readonly Func<string?> _clubCodeProvider;

    internal EmailPersistenceService(
        IEmailPersistenceRepository? repository = null,
        Func<string?>? clubCodeProvider = null)
    {
        _repository = repository ?? new SqlEmailPersistenceRepository();
        _clubCodeProvider = clubCodeProvider ?? (() => SystemUtilities.AppSettings.GetSetting("clubCode"));
    }

    public string ResolveClubCode()
        => _clubCodeProvider()
            ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

    public async Task<HashSet<string>> LaadUitgeslotenAdressenAsync(ILogger log)
    {
        var adressen = await _repository.GetExcludedEmailAddressesAsync(ResolveClubCode());
        log.LogInformation("Uitsluitingslijst geladen: {Aantal} adressen", adressen.Count);
        return adressen;
    }

    public Task<bool> BestaatMessageIdAsync(string messageId)
        => _repository.BestaatMessageIdAsync(messageId);

    public Task<int> InsertEmailVerwerkingAsync(InkomendBericht email)
        => _repository.InsertEmailVerwerkingAsync(email);

    public Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData)
        => _repository.UpdateStatusAsync(verwerkingId, status, geextraheerdeData);

    public Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson)
        => _repository.UpdatePlannerResponseAsync(verwerkingId, plannerResponseJson);

    public Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail)
        => _repository.UpdateAntwoordVerstuurdAsync(verwerkingId, verstuurdNaar, antwoordEmail);

    public Task UpdateFoutAsync(string messageId, string foutMelding)
        => _repository.UpdateFoutAsync(messageId, foutMelding);

    public Task<(bool IsReply, int? OrigineleVerwerkingId, string? OrigineelType, string? OriginaleSamenvatting)>
        DetecteerReplyOpOnsAntwoordAsync(string conversationId, ILogger log)
        => _repository.DetecteerReplyOpOnsAntwoordAsync(conversationId, ResolveClubCode(), log);

    public Task UpdateReplyStatusAsync(int verwerkingId, bool isReply, int replyOpVerwerkingId)
        => _repository.UpdateReplyStatusAsync(verwerkingId, isReply, replyOpVerwerkingId);

    public Task InsertClassificatieCorrectieAsync(
        int origineleVerwerkingId,
        int correctionVerwerkingId,
        string origineelType,
        string? afgeleidType,
        string? originaleSamenvatting,
        string? correctieSamenvatting)
        => _repository.InsertClassificatieCorrectieAsync(
            origineleVerwerkingId,
            correctionVerwerkingId,
            origineelType,
            afgeleidType,
            originaleSamenvatting,
            correctieSamenvatting,
            ResolveClubCode());

    public Task<List<ClassificatieCorrectieVoorbeeld>> HaalLeermomentVoorbeeldenOpAsync(ILogger log)
        => _repository.HaalLeermomentVoorbeeldenOpAsync(ResolveClubCode(), log);
}
