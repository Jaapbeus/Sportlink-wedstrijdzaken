using FunctionApp.Postgres.TeamResolution;
using Npgsql;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Eén team uit onze eigen database, verrijkt met wat het oefenwedstrijd-formulier (#1116) nodig
/// heeft om een <c>ClubMatch</c>-aanvraag te vullen zonder Sportlink-picklist.
/// </summary>
/// <param name="TeamNaam">Canonieke teamnaam zoals in <c>public.teams</c>.</param>
/// <param name="Leeftijdscategorie">
/// Leeftijdscategorie uit <c>public.teams</c> (bijv. <c>JO10</c>; senioren <c>1-99</c>) — dezelfde
/// vorm die het oude formulier als vrije tekst voor <c>AgeClassCode</c> vroeg. ONBEVESTIGD of
/// Sportlink Club precies deze code verwacht; zie <see cref="Planner.Shared.Integrations.SportlinkClub.SportlinkClubMatchAanvraag"/>.
/// </param>
/// <param name="SportlinkTeamId">
/// Het team-ID dat de Sportlink-dataservice zelf hanteert: <c>his.teams.teamcode</c> van de
/// KNVB-rij(en) die als <b>gevalideerde alias</b> aan dit canonieke team hangen
/// (<c>public.teamaliassen</c>, gevuld door <c>TeamCanonicalisatieService</c> na elke sync — regel
/// 4 van docs/ARCHITECTUUR-TEAMRESOLUTIE.md: een alias is pas waarheid na validatie). Alleen gevuld
/// als álle gekoppelde KNVB-rijen hetzelfde ID dragen; bij 0 of meer dan 1 verschillend ID blijft
/// dit <c>null</c> en zegt <paramref name="AantalKandidaatIds"/> waarom. Onderbouwing (#1116,
/// lokale data): voor 103 van de 108 eigen thuisteams in <c>his.matches</c> is <c>thuisteamid</c>
/// exact deze <c>teamcode</c>; 98 van de 104 actieve teams krijgen zo precies één ID, geen enkel
/// team een dubbelzinnig ID. Of Sportlink Club voor <c>PublicHomeTeamId</c> hetzelfde ID gebruikt
/// is ONBEVESTIGD (kan ook een publiek string-ID zijn) — pas te bewijzen met de netwerktrace uit #997.
/// </param>
/// <param name="AantalKandidaatIds">Aantal verschillende <c>teamcode</c>s onder de gevalideerde aliassen (0 = geen KNVB-rij bekend, 1 = eenduidig, &gt;1 = dubbelzinnig).</param>
internal sealed record ClubMatchTeamKoppeling(string TeamNaam, string? Leeftijdscategorie, long? SportlinkTeamId, int AantalKandidaatIds);

/// <summary>
/// Leesqueries voor het oefenwedstrijd-formulier (#1116): teams en velden komen uit onze eigen
/// database, niet uit Sportlink-picklists. Alleen de Postgres-tier heeft de Sportlink Web
/// Extension, dus er is bewust geen SQL Server-tegenhanger (zie
/// docs/ARCHITECTUUR-DATABASE-TIERS.md — geen gedeelde abstractie). Bevat bewust géén eigen
/// teamnaam-logica: de koppeling lokale ↔ KNVB-schrijfwijze loopt uitsluitend via de aliastabel
/// die <see cref="TeamCanonicalisatieService"/> vult (docs/ARCHITECTUUR-TEAMRESOLUTIE.md, regel 1).
/// </summary>
internal static class SportlinkClubMatchRepository
{
    /// <summary>
    /// Zoekt een actief clubteam op naam (hoofdletterongevoelig) en haalt het Sportlink-team-ID
    /// erbij via de gevalideerde aliassen → <c>his.teams.teamcode</c>. Geeft <c>null</c> als het
    /// team niet bestaat of niet actief is — de aanroeper vertaalt dat naar een 400.
    /// </summary>
    internal static async Task<ClubMatchTeamKoppeling?> GetTeamKoppelingAsync(string clubCode, string teamNaam, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($@"
            SELECT p.teamnaam, p.leeftijdscategorie, k.sportlinkteamid, k.aantal
            FROM public.teams p
            CROSS JOIN LATERAL (
                SELECT count(DISTINCT h.teamcode)::int AS aantal,
                       CASE WHEN count(DISTINCT h.teamcode) = 1 THEN min(h.teamcode) END AS sportlinkteamid
                FROM public.teamaliassen a
                JOIN his.teams h
                  ON h.clubcode = a.clubcode
                 AND upper(h.teamnaam) = upper(a.ruwetekst)
                 AND h.mta_deleted IS NULL
                 AND h.teamcode <> -1
                WHERE a.teamid = p.teamid
                  AND a.status = '{TeamAliasConstanten.StatusValidated}'
            ) k
            WHERE p.clubcode = @cc
              AND p.isactief = TRUE
              AND upper(p.teamnaam) = upper(@naam)
            LIMIT 1", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("naam", teamNaam.Trim());
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new ClubMatchTeamKoppeling(
            r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetInt64(2),
            r.GetInt32(3));
    }

    /// <summary>Naam van een actief veld van deze club, of <c>null</c> als het veldnummer onbekend/inactief is.</summary>
    internal static async Task<string?> GetActiefVeldNaamAsync(string clubCode, int veldNummer, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT veldnaam FROM public.velden
            WHERE clubcode = @cc AND veldnummer = @nr AND actief = TRUE", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("nr", veldNummer);
        return await cmd.ExecuteScalarAsync() as string;
    }
}
