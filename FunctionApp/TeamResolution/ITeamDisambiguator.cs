namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Kiest één team uit een korte kandidatenlijst wanneer de deterministische resolutie
/// meerdere teams overhoudt (#697). Forced choice: de implementatie mag alleen een van de
/// aangeboden kandidaten teruggeven, of expliciet niets — nooit een nieuw/verzonnen team.
/// </summary>
public interface ITeamDisambiguator
{
    /// <summary>
    /// Retourneert het gekozen <c>TeamId</c>, of <c>null</c> als er geen betrouwbare keuze te
    /// maken is (dan moet de aanroepende code terugvragen aan de afzender).
    /// </summary>
    Task<int?> KiesAsync(string ruweTekst, IReadOnlyList<TeamCandidate> kandidaten, CancellationToken ct = default);
}
