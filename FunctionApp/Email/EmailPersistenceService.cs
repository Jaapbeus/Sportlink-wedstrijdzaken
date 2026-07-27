using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Email;

/// <summary>
/// Stand van een bestaande verwerkingsrij, zoals de idempotentie-guard die nodig heeft (#712).
/// <para>
/// <c>AntwoordVerstuurd</c> is afgeleid van <c>VerstuurdNaar IS NOT NULL</c> en is de harde grens
/// tegen een dubbel antwoord: die kolom wordt uitsluitend gevuld nádat een antwoord daadwerkelijk
/// de deur uit is. De status alleen is onvoldoende — een verzendfout ná de insert laat de rij achter
/// met een niet-definitieve status.
/// </para>
/// <para>
/// Let op: de AVG-retentie (<c>planner.sp_CleanupEmailVerwerking</c>) zet <c>VerstuurdNaar</c> na 30
/// dagen op NULL. Daarom blijft de statuslijst een tweede, onafhankelijke grens — die wordt door de
/// anonimisering niet gewist.
/// </para>
/// </summary>
internal sealed record EmailVerwerkingStand(
    int VerwerkingId,
    string Status,
    int Pogingen,
    bool AntwoordVerstuurd);

internal interface IEmailPersistenceService
{
    Task<HashSet<string>> LaadUitgeslotenAdressenAsync(ILogger log);

    /// <summary>
    /// Haalt de stand van een eerdere verwerking op, of <c>null</c> als het bericht nog niet
    /// geregistreerd is. Vervangt de oude bestaat-of-niet-check: het bestaan van een rij zegt niets
    /// over de vraag of het bericht ook daadwerkelijk is afgehandeld.
    /// </summary>
    Task<EmailVerwerkingStand?> HaalVerwerkingStandOpAsync(string messageId);

    Task<int> InsertEmailVerwerkingAsync(InkomendBericht email);

    /// <summary>Verhoogt de pogingenteller van een bestaande verwerking met één.</summary>
    Task VerhoogPogingenAsync(int verwerkingId);

    Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData);
    Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson);
    Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail);

    /// <summary>
    /// Slaat een voorgesteld antwoord op zonder het te versturen (review mode) en zet de status op
    /// <see cref="EmailStatus.Review"/>. Vult bewust géén <c>VerstuurdNaar</c> — er is niets
    /// verstuurd, en die kolom is de duplicaatgrens van de idempotentie-guard.
    /// </summary>
    Task UpdateVoorgesteldAntwoordAsync(int verwerkingId, string antwoordEmail);

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

    public Task<EmailVerwerkingStand?> HaalVerwerkingStandOpAsync(string messageId)
        => _repository.HaalVerwerkingStandOpAsync(messageId);

    public Task<int> InsertEmailVerwerkingAsync(InkomendBericht email)
        => _repository.InsertEmailVerwerkingAsync(email);

    public Task VerhoogPogingenAsync(int verwerkingId)
        => _repository.VerhoogPogingenAsync(verwerkingId);

    public Task UpdateVoorgesteldAntwoordAsync(int verwerkingId, string antwoordEmail)
        => _repository.UpdateVoorgesteldAntwoordAsync(verwerkingId, antwoordEmail);

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
