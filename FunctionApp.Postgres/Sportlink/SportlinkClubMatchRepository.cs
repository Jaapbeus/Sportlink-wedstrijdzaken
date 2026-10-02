using FunctionApp.Postgres.TeamResolution;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;
using static Planner.Endpoints.Sportlink.ClubMatchEndpointCore;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Leesqueries voor het oefenwedstrijd-formulier (#1116): teams en velden komen uit onze eigen
/// database, niet uit Sportlink-picklists. De SQL Server-tier heeft sinds #1266 een eigen,
/// parallelle implementatie (<c>FunctionApp/Sportlink/SportlinkClubMatchRepository.cs</c>) — geen
/// gedeelde providerabstractie, conform docs/ARCHITECTUUR-DATABASE-TIERS.md. Bevat bewust géén eigen
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
        return (await LeesAlleAsync(cmd, TeamKoppelingRij)).FirstOrDefault();
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

    /// <summary>Alle actieve velden van deze club (#1339) — bron voor de veld-dropdown in
    /// <c>SportlinkMatchPanel</c>, zodat de beheerder een veld op onze eigen naam kiest in plaats
    /// van Sportlinks <c>FieldId</c>-formaat te moeten kennen.</summary>
    internal static async Task<List<(int VeldNummer, string VeldNaam)>> GetActieveVeldenAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT veldnummer, veldnaam FROM public.velden
            WHERE clubcode = @cc AND actief = TRUE
            ORDER BY veldnummer", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await LeesAlleAsync(cmd, r => (r.GetInt32(0), r.GetString(1)));
    }

    /// <summary>Actieve teams (met leeftijdscategorie) én speeltijden van deze club (#1437) — bron voor de
    /// voorinvulling van "Wedstrijd aanmaken". Geen teamnaam-logica: de koppeling met de speeltijden
    /// gebeurt in <c>ClubMatchEndpointCore</c> (Planner.Shared-normalisatie).</summary>
    internal static async Task<(List<ClubMatchFormulierTeamInvoer> Teams, List<ClubMatchSpeeltijdInvoer> Speeltijden)> GetFormulierGegevensAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var teamsCmd = new NpgsqlCommand(
            "SELECT teamnaam, leeftijdscategorie FROM public.teams WHERE clubcode = @cc AND isactief = TRUE ORDER BY teamnaam", conn);
        teamsCmd.Parameters.AddWithValue("cc", clubCode);
        await using var tijdenCmd = new NpgsqlCommand(
            "SELECT leeftijd, veldafmeting, wedstrijdtotaal FROM public.speeltijden WHERE clubcode = @cc", conn);
        tijdenCmd.Parameters.AddWithValue("cc", clubCode);
        return (await LeesAlleAsync(teamsCmd, TeamRij), await LeesAlleAsync(tijdenCmd, SpeeltijdRij));
    }

    /// <summary>Clubinstelling Spelactiviteit (#1437, migratie 032) — leeg of ontbrekend is <c>null</c>.</summary>
    internal static async Task<string?> GetSpelactiviteitAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT sportlinkspelactiviteit FROM public.appsettings WHERE clubcode = @cc LIMIT 1", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>
    /// Reserveert het volgende volgnummer voor deze speeldag (#1437), atomair in één statement: een
    /// <c>INSERT ... ON CONFLICT DO UPDATE</c> is voor gelijktijdige aanvragen een rijslot, dus twee
    /// aanvragen krijgen nooit hetzelfde nummer. <c>null</c> als de dag vol zit (het volgnummer blijft
    /// dan op het maximum staan).
    /// </summary>
    internal static async Task<int?> ReserveerVolgnummerAsync(string clubCode, DateOnly datum, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.wedstrijdnummerteller AS t (clubcode, datum, laatstevolgnummer)
            VALUES (@cc, @datum, 1)
            ON CONFLICT (clubcode, datum) DO UPDATE
                SET laatstevolgnummer = t.laatstevolgnummer + 1
                WHERE t.laatstevolgnummer < @max
            RETURNING laatstevolgnummer", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("datum", datum);
        cmd.Parameters.AddWithValue("max", ClubMatchWedstrijdNummer.MaxVolgnummer);
        return await cmd.ExecuteScalarAsync() as int?;
    }
}
