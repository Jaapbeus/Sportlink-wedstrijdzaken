namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Eén regel van een officials-toewijzing (#994, epic #986) — positie + persoonsidentifier, NOOIT
/// een naam (AVG: deze app toont/verstuurt uitsluitend wat de beheerder zelf intikt, roept nooit
/// een Sportlink-zoek-/personendetail-endpoint aan).
/// <para>
/// <b>ONBEVESTIGD (#994):</b> de exacte elementstructuur van Sportlinks
/// <c>OfficialsToBeAssigned</c> is nooit live gezien (geen netwerktrace) — afgeleid uit
/// <c>OfficialPosition</c> zoals dat terugkomt in <c>GET .../MatchOfficials</c>. Zowel de veldnamen
/// als de exacte persoonsidentifier-naam (hier aangenomen: <c>PersoonId</c>) kunnen afwijken van
/// wat Sportlink werkelijk verwacht. Deze aanname blijft in gebruik totdat een mens (nooit een
/// agent, zie docs/SPORTLINK-WEB-EXTENSION.md §4.4) een live trace heeft gedaan — zie
/// <c>SportlinkClubClient.MatchOfficialsActionLiveBevestigd</c>.
/// </para>
/// </summary>
/// <param name="OfficialPosition">Positiecode zoals Sportlink die hanteert (bijv. scheidsrechter/AR1/AR2 — exacte waarden ONBEVESTIGD).</param>
/// <param name="PersoonId">Relatiecode/persoons-identifier zoals door de beheerder ingevoerd — geen naam, geen zoekfunctie (AVG, #994).</param>
public sealed record SportlinkOfficialToewijzing(string OfficialPosition, string PersoonId);
