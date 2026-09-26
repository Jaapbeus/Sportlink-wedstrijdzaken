namespace Planner.Shared.Integrations.SportlinkClub;

// De leesmodellen van de PublicMatchId-cache en het oefenwedstrijd-formulier (#991, #1017, #1111,
// #1116). Ze stonden tot #1266 als `internal record` in de Postgres-tier; bij het herstellen van de
// SQL Server-tier zouden ze anders letterlijk verdubbeld zijn. De vórm van deze gegevens is
// tier-onafhankelijk — alleen de query die ze vult verschilt (Npgsql vs. SqlClient), en die blijft
// per tier.

/// <summary>Interne wedstrijdgegevens nodig voor de #987-reverse-lookup — niet de volledige
/// <c>his.matches</c>-rij, alleen wat <see cref="SportlinkClubClient.ResolvePublicMatchIdAsync"/>
/// nodig heeft.</summary>
public sealed record WedstrijdVoorLookup(long Wedstrijdnummer, DateOnly Datum);

/// <summary>Eén rij zonder cache-entry, gebruikt door de #1017-warmup-timer — bevat ook
/// <c>Wedstrijdcode</c> (de eigen sleutel om straks in de cache te schrijven), in tegenstelling tot
/// <see cref="WedstrijdVoorLookup"/> dat alleen is wat de Sportlink-aanroep zelf nodig heeft.</summary>
public sealed record WedstrijdZonderCache(long Wedstrijdcode, long Wedstrijdnummer, DateOnly Datum);

/// <summary>
/// Eigen wedstrijdcontext bij een wijzigingsverzoek (#1111, epic #986): wat <c>/wijzigingsverzoeken</c>
/// nodig heeft om een verzoek herkenbaar te maken — wedstrijdnummer, teams, datum, tijd,
/// accommodatie. Komt uit de eigen wedstrijdhistorie via de PublicMatchId-cache (#991), NIET uit
/// extra velden van Sportlinks <c>MatchChangeRequests</c>-respons: die velden zijn nooit met een
/// live netwerktrace bevestigd (issue #1111, "bekende technische aanname"), en een netwerktrace mag
/// nooit door een agent gemaakt worden (docs/SPORTLINK-WEB-EXTENSION.md §4.4). Onze eigen data is
/// er al en is gegarandeerd juist voor onze eigen wedstrijden.
/// <para>
/// <c>null</c> op het verzoek betekent: geen cache-treffer voor dit <c>PublicMatchId</c> — de
/// wedstrijd is nog niet door de warmup-timer (#1017) of een paneel-lookup geraakt, of het is geen
/// wedstrijd van deze club. Het verzoek blijft dan gewoon zichtbaar, zonder context.
/// </para>
/// </summary>
public sealed record SportlinkWedstrijdContext(
    long Wedstrijdcode,
    long? Wedstrijdnummer,
    string? Thuisteam,
    string? Uitteam,
    string? Datum,
    string? Tijd,
    string? Accommodatie);

/// <summary>
/// Eén team uit onze eigen database, verrijkt met wat het oefenwedstrijd-formulier (#1116) nodig
/// heeft om een <c>ClubMatch</c>-aanvraag te vullen zonder Sportlink-picklist.
/// </summary>
/// <param name="TeamNaam">Canonieke teamnaam zoals in de eigen teamtabel.</param>
/// <param name="Leeftijdscategorie">
/// Leeftijdscategorie uit de eigen teamtabel (bijv. <c>JO10</c>; senioren <c>1-99</c>) — dezelfde
/// vorm die het oude formulier als vrije tekst voor <c>AgeClassCode</c> vroeg. ONBEVESTIGD of
/// Sportlink Club precies deze code verwacht; zie <see cref="SportlinkClubMatchAanvraag"/>.
/// </param>
/// <param name="SportlinkTeamId">
/// Het team-ID dat de Sportlink-dataservice zelf hanteert: <c>his.teams.teamcode</c> van de
/// KNVB-rij(en) die als <b>gevalideerde alias</b> aan dit canonieke team hangen (de aliastabel,
/// gevuld door de teamcanonicalisatie na elke sync — regel 4 van
/// docs/ARCHITECTUUR-TEAMRESOLUTIE.md: een alias is pas waarheid na validatie). Alleen gevuld als
/// álle gekoppelde KNVB-rijen hetzelfde ID dragen; bij 0 of meer dan 1 verschillend ID blijft dit
/// <c>null</c> en zegt <paramref name="AantalKandidaatIds"/> waarom. Of Sportlink Club voor
/// <c>PublicHomeTeamId</c> hetzelfde ID gebruikt is ONBEVESTIGD (kan ook een publiek string-ID
/// zijn) — pas te bewijzen met de netwerktrace uit #997.
/// </param>
/// <param name="AantalKandidaatIds">Aantal verschillende <c>teamcode</c>s onder de gevalideerde aliassen (0 = geen KNVB-rij bekend, 1 = eenduidig, &gt;1 = dubbelzinnig).</param>
public sealed record ClubMatchTeamKoppeling(
    string TeamNaam, string? Leeftijdscategorie, long? SportlinkTeamId, int AantalKandidaatIds);
