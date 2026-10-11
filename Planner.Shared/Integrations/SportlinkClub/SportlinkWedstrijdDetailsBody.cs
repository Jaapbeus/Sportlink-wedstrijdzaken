
namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Alleen de velden die een pad mag overschrijven; <c>null</c> = waarde uit de snapshot houden (#1626).
/// Een lege tekst of nul telt dus als overschrijving, alleen <c>null</c> niet.
/// </summary>
internal sealed record MatchDataOverschrijving(
    string? FieldId = null, string? FieldSize = null, int? FieldOffset = null,
    string? FacilityId = null, string? MatchDate = null, string? StartTime = null,
    string? MatchChangeRequestRemarks = null);

// ── Uitgaande envelope voor UpdateMatchDetails (PascalCase = de wire-vorm, geen
// JsonPropertyName nodig — zelfde patroon als de bestaande PutDressingRoomsAsync/-body's). ──

internal sealed record UpdateMatchDetailsBody(
    object? ConfirmationNeeded,
    bool IsForceUpdate,
    bool IsMatchChangeRequestMandatory,
    bool IsOwnFacility,
    bool IsPlannableByClub,
    bool IsSuccess,
    string PublicApplicantId,
    string PublicMatchId,
    MatchDataBody MatchData);

internal sealed record MatchDataBody(
    string? AgeClassCode,
    string? AssemblyTime,
    int? AwayScore,
    string? AwayTeam,
    string? DepartureTime,
    string? Description,
    string? Drivers,
    int? Duration,
    long? ExternalMatchId,
    string? FacilityId,
    string? FieldId,
    int? FieldOffset,
    string? FieldSize,
    int? HomeScore,
    string? HomeTeam,
    string? MatchChangeRequestRemarks,
    string? MatchDate,
    string? MatchStatus,
    string? PublicAwayTeamId,
    string? PublicHomeTeamId,
    string? SportIdTag,
    string? StartTime);

/// <summary>Stateloze opbouw van de <c>UpdateMatchDetails</c>-envelope uit een snapshot (#1626, #1631).</summary>
internal static class SportlinkWedstrijdDetailsBody
{
    /// <summary>
    /// Gedeelde opbouw van <c>MatchData</c> voor beide schrijfpaden (#1626): alles uit de snapshot,
    /// behalve de overschreven velden. In twee stappen om elke stap klein te houden: eerst een
    /// volledige kopie van de snapshot, dan alleen de overschrijfbare velden. De golden-tests
    /// leggen de volledige body van beide paden vast.
    /// </summary>
    internal static MatchDataBody BouwMatchData(SportlinkMatchDetailsSnapshot snapshot, MatchDataOverschrijving o)
        => PasOverschrijvingToe(MatchDataUitSnapshot(snapshot), o);

    private static MatchDataBody MatchDataUitSnapshot(SportlinkMatchDetailsSnapshot snapshot) => new(
        AgeClassCode: snapshot.AgeClassCode,
        AssemblyTime: snapshot.MatchDetails?.MatchDetailsHome?.AssemblyTime?.Value,
        AwayScore: snapshot.Result?.AwayScore,
        AwayTeam: snapshot.Teams?.Away?.TeamName,
        DepartureTime: null,
        Description: snapshot.MatchDetails?.Description?.Value,
        Drivers: "",
        Duration: snapshot.Duration,
        ExternalMatchId: snapshot.ExternalMatchId,
        FacilityId: snapshot.MatchField?.FacilityId,
        FieldId: snapshot.Field?.FieldId,
        FieldOffset: snapshot.Field?.FieldOffset,
        FieldSize: snapshot.Field?.FieldSize,
        HomeScore: snapshot.Result?.HomeScore,
        HomeTeam: snapshot.Teams?.Home?.TeamName,
        MatchChangeRequestRemarks: "",
        MatchDate: snapshot.MatchDate?.Date,
        MatchStatus: snapshot.MatchStatus,
        PublicAwayTeamId: snapshot.Teams?.Away?.PublicTeamId,
        PublicHomeTeamId: snapshot.Teams?.Home?.PublicTeamId,
        SportIdTag: snapshot.Sport?.IdTag,
        StartTime: snapshot.MatchDate?.StartTime);

    private static MatchDataBody PasOverschrijvingToe(MatchDataBody basis, MatchDataOverschrijving o) => basis with
    {
        FacilityId = o.FacilityId ?? basis.FacilityId,
        FieldId = o.FieldId ?? basis.FieldId,
        FieldOffset = o.FieldOffset ?? basis.FieldOffset,
        FieldSize = o.FieldSize ?? basis.FieldSize,
        MatchChangeRequestRemarks = o.MatchChangeRequestRemarks ?? basis.MatchChangeRequestRemarks,
        MatchDate = o.MatchDate ?? basis.MatchDate,
        StartTime = o.StartTime ?? basis.StartTime
    };

    /// <summary>
    /// Bouwt de <c>UpdateMatchDetails</c>-envelope voor #995 — zelfde patroon als
    /// <c>PutMatchDetailsAsync</c> (snapshot terugsturen met alléén het gewijzigde deel
    /// overschreven), maar met <c>MatchDate</c>/<c>StartTime</c>/<c>FacilityId</c>/
    /// <c>MatchChangeRequestRemarks</c> als overschrijfbare velden in plaats van het veld. Hergebruikt
    /// bewust de bestaande <see cref="MatchDataBody"/>/<see cref="UpdateMatchDetailsBody"/>-records
    /// (pure databehouders, geen logica); het samenstellen van de <c>MatchData</c> loopt sinds #1626
    /// via de gedeelde <see cref="BouwMatchData"/>, de envelope zelf blijft een eigen bouw.
    /// <para>
    /// <b>ONBEVESTIGD (#995):</b> <c>IsMatchChangeRequestMandatory: true</c> en een lege
    /// <c>PublicApplicantId</c> zijn aannames — #993's veld-wijziging gebruikt <c>false</c> resp. een
    /// live-bevestigde lege string voor een EIGEN-veld-wijziging, geen wijzigingsverzoek aan een
    /// tegenstander. Beide zijn nooit met een netwerktrace gezien voor dit specifieke pad.
    /// </para>
    /// </summary>
    internal static object BuildMatchChangeRequestBody(
        string publicMatchId, SportlinkMatchDetailsSnapshot snapshot,
        DateOnly? nieuweDatum, TimeOnly? nieuweStartTijd, string? nieuweFacilityId, string toelichting)
    {
        var matchData = BouwMatchData(snapshot, new MatchDataOverschrijving(
            FacilityId: nieuweFacilityId,
            MatchDate: nieuweDatum?.ToString("yyyy-MM-dd"),
            StartTime: nieuweStartTijd?.ToString("HH:mm:ss"),
            MatchChangeRequestRemarks: toelichting));

        return new UpdateMatchDetailsBody(
            ConfirmationNeeded: null,
            IsForceUpdate: false,
            IsMatchChangeRequestMandatory: true,
            IsOwnFacility: true,
            IsPlannableByClub: false,
            IsSuccess: false,
            PublicApplicantId: "",
            PublicMatchId: publicMatchId,
            MatchData: matchData);
    }
}
