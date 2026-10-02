namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Vertaalt Sportlinks <c>ChangeRequestStatus</c> naar de vier groepen van Sportlinks eigen filter
/// (bron: Sportlinks frontend-bundle, #1439): Openstaand = CONFIRM_AWAY/CONFIRM_HOME/CONFIRM_UNION,
/// Akkoord = APPROVED/MATCH_FINALIZED, Afgewezen = DENIED, Ingetrokken = REVOKED.
/// Alles anders (ook leeg of een toekomstige waarde) is <see cref="Onbekend"/>.
/// </summary>
public static class SportlinkChangeRequestStatusGroep
{
    public const string Openstaand = "OPEN";
    public const string Akkoord = "ACCEPTED";
    public const string Afgewezen = "DENIED";
    public const string Ingetrokken = "REVOKED";
    public const string Onbekend = "UNKNOWN";

    public static string Bepaal(string? changeRequestStatus)
        => changeRequestStatus?.Trim().ToUpperInvariant() switch
        {
            "CONFIRM_AWAY" or "CONFIRM_HOME" or "CONFIRM_UNION" => Openstaand,
            "APPROVED" or "MATCH_FINALIZED" => Akkoord,
            "DENIED" => Afgewezen,
            "REVOKED" => Ingetrokken,
            _ => Onbekend,
        };
}
