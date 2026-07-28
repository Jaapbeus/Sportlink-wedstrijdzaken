using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Email;

/// <summary>
/// Stand van een bestaande verwerkingsrij, zoals de idempotentie-guard die nodig heeft (#712).
/// <para>
/// <c>AntwoordVerstuurd</c> is de harde grens tegen een dubbel antwoord. Sinds #718 komt die uit
/// <c>IsBeantwoord</c> — een boolean die de AVG-anonimisering overleeft — met <c>VerstuurdNaar
/// IS NOT NULL</c> als terugvalpad voor rijen van vóór die kolom. Voorheen was <c>VerstuurdNaar</c>
/// de enige bron, en die wordt na 30 dagen leeggemaakt.
/// </para>
/// <para>
/// De statuslijst blijft daarnaast een tweede, onafhankelijke grens: bij een rij die al geanonimiseerd
/// was vóór de introductie van <c>IsBeantwoord</c> is het feit "er is geantwoord" niet meer uit de
/// data te herleiden.
/// </para>
/// </summary>
/// <param name="VerzendPogingOnbeslist">
/// Er is een verzendpoging vastgelegd waarvan de uitkomst onbekend is (#716): de intentie werd gezet
/// vlak vóór het versturen en is niet gewist, terwijl er ook geen antwoord is vastgelegd. Dat wijst op
/// een harde afbreking tussen versturen en vastleggen. Opnieuw versturen is dan niet veilig, want het
/// eerste antwoord kan de deur al uit zijn.
/// </param>
internal sealed record EmailVerwerkingStand(
    int VerwerkingId,
    string Status,
    int Pogingen,
    bool AntwoordVerstuurd,
    bool VerzendPogingOnbeslist = false);

internal interface IEmailPersistenceService
{
    Task<HashSet<string>> LaadUitgeslotenAdressenAsync(ILogger log);

    /// <summary>
    /// Haalt de stand van een eerdere verwerking op, of <c>null</c> als het bericht nog niet
    /// geregistreerd is. Vervangt de oude bestaat-of-niet-check: het bestaan van een rij zegt niets
    /// over de vraag of het bericht ook daadwerkelijk is afgehandeld.
    /// </summary>
    Task<EmailVerwerkingStand?> HaalVerwerkingStandOpAsync(string messageId);

    /// <summary>
    /// Registreert een nieuw bericht en geeft het verwerkingId terug. Gooit
    /// <see cref="DubbeleMessageIdException"/> als een gelijktijdige invocatie dezelfde MessageId al
    /// heeft vastgelegd — dat is geen verwerkingsfout en mag niet als zodanig worden weggeschreven.
    /// </summary>
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

    /// <summary>
    /// Legt de intentie vast om te gaan versturen, vóór de verzendpoging (#716). Zie
    /// <see cref="WisVerzendPogingAsync"/> voor het wissen bij een aantoonbaar mislukte poging.
    /// </summary>
    Task MarkeerVerzendPogingAsync(int verwerkingId);

    /// <summary>
    /// Wist de verzendintentie omdat het versturen aantoonbaar is mislukt (#716). Alleen dán mag een
    /// volgende poging opnieuw versturen; blijft de intentie staan, dan is de uitkomst onbekend.
    /// </summary>
    Task WisVerzendPogingAsync(int verwerkingId);

    /// <summary>
    /// Legt een verwerkingsfout vast op <b>verwerkingId</b> (#717), consistent met alle andere
    /// mutaties. Overschrijft nooit een rij waarvan al vaststaat dat er een antwoord op is verstuurd.
    /// </summary>
    Task UpdateFoutAsync(int verwerkingId, string foutMelding);
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

    /// <summary>
    /// Resolveert de ClubCode-discriminator en weigert óók een lege waarde. <c>?? throw</c> was niet
    /// genoeg: <c>LoadSettingsAsync</c> zet een lege kolomwaarde als <c>""</c> in de settings-cache,
    /// en met ClubCode <c>""</c> levert de query voor de uitsluitingslijst een lege set op —
    /// uitgesloten adressen werden dan alsnog verwerkt en beantwoord. Fail-open op een AVG-maatregel
    /// is nooit acceptabel, dus liever hard falen. (#707)
    /// </summary>
    public string ResolveClubCode()
    {
        var clubCode = _clubCodeProvider();
        if (string.IsNullOrWhiteSpace(clubCode))
            throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");
        return clubCode;
    }

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

    public Task MarkeerVerzendPogingAsync(int verwerkingId)
        => _repository.MarkeerVerzendPogingAsync(verwerkingId);

    public Task WisVerzendPogingAsync(int verwerkingId)
        => _repository.WisVerzendPogingAsync(verwerkingId);

    public Task UpdateFoutAsync(int verwerkingId, string foutMelding)
        => _repository.UpdateFoutAsync(verwerkingId, foutMelding);

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
