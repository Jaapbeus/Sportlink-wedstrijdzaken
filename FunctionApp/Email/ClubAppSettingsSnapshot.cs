namespace SportlinkFunction.Email;

/// <summary>
/// Club-specifieke instellingen-snapshot voor het dry-run pad van de Email-tester (#677).
///
/// De echte e-mailpipeline (<c>EmailProcessorFunction</c>, timer/queue-triggered, geen
/// club-switcher) leest deze velden rechtstreeks uit de proces-globale
/// <see cref="SportlinkFunction.SystemUtilities.AppSettings"/> cache — dat is correct, want voor
/// dat kanaal is er altijd precies één (primaire) club.
///
/// De Admin GUI's Email-tester (<c>EmailTestFunction</c>, HTTP-getriggerd) kan via de
/// club-switcher echter ook AllStars FC selecteren. Zonder deze snapshot bleven
/// <c>BerichtPipeline</c> en <c>BerichtResponseGenerator</c> de globale cache lezen, waardoor een
/// AllStars-dry-run altijd de instellingen (afzendernaam, coördinator, voetnoot) van de echte
/// productieclub gebruikte — zie issue #677.
///
/// Alle velden zijn bewust nullable: een ontbrekende waarde in <c>dbo.AppSettings</c> voor de
/// opgegeven club betekent "geen waarde ingesteld voor déze club", nooit "val terug op de
/// instelling van een andere club".
/// </summary>
public sealed record ClubAppSettingsSnapshot(
    string? PlannerAfzenderNaam,
    string? CoordinatorNaam,
    string? CoordinatorFunctie,
    string? EmailVoetnoot,
    int? HerplanDeadlineDagen,
    // #561: verzet-zonder-datum flow (KNVB-bijlage + vrije-zaterdagen-voorzet)
    bool? KnvbPdfBijlageIngeschakeld = null,
    string? KnvbStandaardRegio = null);
