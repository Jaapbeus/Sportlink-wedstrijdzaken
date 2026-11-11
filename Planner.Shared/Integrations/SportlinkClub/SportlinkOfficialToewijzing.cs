namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Eén regel van een officials-toewijzing (#994, epic #986) — positie + persoonsidentifier, NOOIT
/// een naam (AVG: deze app toont/verstuurt uitsluitend wat de beheerder zelf intikt, roept nooit
/// een Sportlink-zoek-/personendetail-endpoint aan).
/// <para>
/// De eigenaar heeft het endpoint na een live trace van #1319 vrijgegeven; deze aanroep volgt de
/// instelling <c>sportlinkDryRun</c>. De contractnamen <c>OfficialsToBeAssigned</c> en
/// <c>PersoonId</c> blijven modelaannames. De exacte positiewaarden blijven onbevestigd.
/// </para>
/// </summary>
/// <param name="OfficialPosition">Positiecode zoals Sportlink die hanteert (bijv. scheidsrechter/AR1/AR2 — exacte waarden ONBEVESTIGD).</param>
/// <param name="PersoonId">Relatiecode/persoons-identifier zoals door de beheerder ingevoerd — geen naam, geen zoekfunctie (AVG, #994).</param>
public sealed record SportlinkOfficialToewijzing(string OfficialPosition, string PersoonId);
