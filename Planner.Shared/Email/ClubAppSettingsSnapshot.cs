namespace Planner.Shared.Email;

/// <summary>
/// Clubinstellingen die een dry-run expliciet meekrijgt. Een ontbrekende waarde voor de gekozen
/// club is null; callers vallen daarmee niet terug op instellingen van een andere club.
/// </summary>
public sealed record ClubAppSettingsSnapshot(
    string? PlannerAfzenderNaam,
    string? CoordinatorNaam,
    string? CoordinatorFunctie,
    string? EmailVoetnoot,
    int? HerplanDeadlineDagen,
    bool? KnvbPdfBijlageIngeschakeld = null,
    string? KnvbStandaardRegio = null,
    bool EmailReviewModusActief = true)
{
    /// <summary>Freeze the existing primary-club cache together with the DB review switch for one message.</summary>
    public static ClubAppSettingsSnapshot VanInstellingen(
        Func<string, string?> lees, bool reviewModusActief)
    {
        var deadlineTekst = lees("herplanDeadlineDagen");
        var pdfTekst = lees("knvbPdfBijlageIngeschakeld");
        return new(
            lees("plannerAfzenderNaam"), lees("coordinatorNaam"), lees("coordinatorFunctie"),
            lees("emailVoetnoot"), int.TryParse(deadlineTekst, out var dagen) ? dagen : null,
            pdfTekst is null ? null : pdfTekst is "1" or "true",
            lees("knvbStandaardRegio"), reviewModusActief);
    }
}
