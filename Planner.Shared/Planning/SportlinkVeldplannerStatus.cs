using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Shared.Planning;

/// <summary>
/// Of, en zo niet waarom niet, de Sportlink-veldplanner is geraadpleegd voor een Planning-antwoord (#1774).
/// <para>
/// WZ-ADR-013: Sportlink is leidend en een afwijking is zichtbaar, nooit stil. Tot #1774 viel de Planning bij élke
/// storing stilzwijgend terug op de databasekopie; dit record reist mee in het API-antwoord zodat de pagina, de
/// deelweergave en het log dezelfde reden noemen.
/// </para>
/// </summary>
/// <param name="Geraadpleegd"><c>true</c> als veld, tijd en duur uit de veldplanner komen.</param>
/// <param name="Reden">Vaste code, zie de <c>Reden*</c>-constanten; <see cref="RedenOk"/> bij succes.</param>
/// <param name="Toelichting">Korte, leesbare uitleg voor de beheerder; nooit een technische waarde.</param>
/// <param name="GeraadpleegdOpUtc">Moment van de (eventueel uit de cache komende) raadpleging; <c>null</c> bij terugval.</param>
public sealed record SportlinkVeldplannerStatus(bool Geraadpleegd, string Reden, string Toelichting, DateTime? GeraadpleegdOpUtc)
{
    public const string RedenOk = "Ok";
    public const string RedenDemoclub = "Democlub";
    public const string RedenGeenAccommodatie = "GeenAccommodatie";
    public const string RedenExtensieUit = "ExtensieUit";
    public const string RedenEgressGeblokkeerd = "EgressGeblokkeerd";
    public const string RedenClientNietGeconfigureerd = "ClientNietGeconfigureerd";
    public const string RedenKoppelingVerlopen = "KoppelingVerlopen";
    public const string RedenRolNietGekoppeld = "RolNietGekoppeld";
    public const string RedenAccommodatieOnbekend = "AccommodatieOnbekend";
    public const string RedenNetwerkFout = "NetwerkFout";
    public const string RedenSportlinkFout = "SportlinkFout";
    public const string RedenOnverwacht = "Onverwacht";

    /// <summary>Veldplanner geraadpleegd: de Planning volgt Sportlink.</summary>
    public static SportlinkVeldplannerStatus Ok(DateTime geraadpleegdOpUtc)
        => new(true, RedenOk, "Veld, aanvangstijd en speelduur komen uit de Sportlink-veldplanner.", geraadpleegdOpUtc);

    /// <summary>Terugval met een vaste reden; de toelichting hoort bij die reden.</summary>
    public static SportlinkVeldplannerStatus Terugval(string reden) => new(false, reden, ToelichtingVoor(reden), null);

    /// <summary>De terugvalreden die bij een Sportlink-aanroepstatus hoort (koppeling verlopen, netwerk, overig).</summary>
    public static SportlinkVeldplannerStatus VanAanroepStatus(SportlinkClubCallStatus status) => status switch
    {
        SportlinkClubCallStatus.HerkoppelingVereist => Terugval(RedenKoppelingVerlopen),
        SportlinkClubCallStatus.RolNietGekoppeld => Terugval(RedenRolNietGekoppeld),
        SportlinkClubCallStatus.NetwerkFout => Terugval(RedenNetwerkFout),
        _ => Terugval(RedenSportlinkFout)
    };

    /// <summary>Of de beheerder de Sportlink-koppeling moet herstellen (beheerpagina), in plaats van wachten.</summary>
    public bool KoppelingHerstelNodig => Reden is RedenKoppelingVerlopen or RedenRolNietGekoppeld;

    private static string ToelichtingVoor(string reden) => reden switch
    {
        RedenDemoclub => "De democlub staat niet in Sportlink; de Planning toont de demogegevens.",
        RedenGeenAccommodatie => "Er is geen accommodatie ingesteld; zonder accommodatie kan de veldplanner niet worden opgevraagd.",
        RedenExtensieUit => "De Sportlink Web Extension staat uit; de Planning toont de databasekopie van de laatste synchronisatie.",
        RedenEgressGeblokkeerd => "Uitgaand verkeer naar Sportlink is in deze omgeving geblokkeerd; de Planning toont de databasekopie.",
        RedenClientNietGeconfigureerd => "De Sportlink-koppeling is in deze omgeving niet geconfigureerd; de Planning toont de databasekopie.",
        RedenKoppelingVerlopen => "De Sportlink-koppeling is verlopen; herstel die via de beheerpagina van de Sportlink Web Extension. Tot die tijd toont de Planning de databasekopie, die kan afwijken van Sportlink.",
        RedenRolNietGekoppeld => "De Sportlink-rol is niet gekoppeld; koppel die via de beheerpagina van de Sportlink Web Extension. Tot die tijd toont de Planning de databasekopie.",
        RedenAccommodatieOnbekend => "De ingestelde accommodatie komt niet voor in de Sportlink-locatielijst; controleer de instelling. De Planning toont de databasekopie.",
        RedenNetwerkFout => "Sportlink reageerde niet op tijd; de Planning toont de databasekopie, die kan afwijken van Sportlink. Probeer het later opnieuw.",
        RedenSportlinkFout => "Sportlink gaf een fout; de Planning toont de databasekopie, die kan afwijken van Sportlink.",
        _ => "De Sportlink-veldplanner kon onverwacht niet worden opgehaald; de Planning toont de databasekopie, die kan afwijken van Sportlink."
    };
}

/// <summary>Het antwoord van <c>GET /api/planner/veldbezetting</c> (#1774): de regels én of Sportlink is geraadpleegd.</summary>
public sealed record VeldbezettingAntwoord<T>(SportlinkVeldplannerStatus Sportlink, IReadOnlyList<T> Wedstrijden)
{
    /// <summary>
    /// De JSON-vorm voor <c>GET /api/planner/veldbezetting</c>. Zonder opt-in (<c>?status=true</c>) blijft de bestaande kale array
    /// bestaan, zodat een reeds geopende of oudere client blijft werken; met opt-in komt de envelope <c>{ sportlink, wedstrijden }</c>.
    /// </summary>
    public object JsonVorm(bool metStatus) => metStatus ? this : Wedstrijden;

    /// <summary>Of de query-parameter <c>status</c> de statusenvelope aanvraagt (alleen de waarde <c>true</c>, hoofdletterongevoelig).</summary>
    public static bool StatusAangevraagd(string? statusParameter)
        => string.Equals(statusParameter?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
