
namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Retry-beleid per aanroeptype (#1417). Elke call site kiest expliciet — er is bewust géén
/// default, zodat een nieuwe mutatie nooit stilzwijgend het leesbeleid erft.
/// </summary>
internal enum RetryBeleid
{
    /// <summary>Idempotente GET: bij een transiënte fout (timeout/netwerk/5xx) één keer
    /// opnieuw na <see cref="SportlinkClubHttp.TransientRetryDelay"/> (#1387).</summary>
    Lezen,

    /// <summary>
    /// Niet-idempotente PUT/POST: <b>géén</b> transiënte retry. Een timeout of gateway-5xx nádat
    /// Sportlink de aanvraag al verwerkt heeft, is voor deze client niet te onderscheiden van
    /// "nooit aangekomen"; herhalen zou dan een tweede oefenwedstrijd aanmaken of een
    /// wijzigingsverzoek tweemaal bij een echte tegenstander afleveren (#1417). De 401-re-auth-
    /// retry blijft wél gelden: die herhaalt pas na een expliciete afwijzing vóór verwerking.
    /// </summary>
    Mutatie
}
