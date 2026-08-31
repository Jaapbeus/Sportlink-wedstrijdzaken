using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres.Planner.Repositories;

/// <summary>
/// Postgres-tier-tegenhanger van (een deel van) <c>FunctionApp/Planner/Repositories/PlannerMatchRepository.cs</c>
/// (#888). <b>Uitsluitend</b> <see cref="MarkeerVervallenGeplandeWedstrijdenAsync"/> is hier
/// vertaald — de overige methoden van die klasse (CheckAvailability, SaveHerplanVerzoekAsync,
/// GetFutureMatchesForTeamAsync, ...) blijven #888's eigen, aanzienlijk grotere scope
/// (11 van de 12 planner-endpoints, zie docs/ARCHITECTUUR-DATABASE-TIERS.md §16).
/// <para>
/// Deze ene methode is apart getrokken omdat #890 hem expliciet als een écht, ongeguard gat in de
/// synchronisatiepijplijn documenteerde (<c>PostgresSyncPipeline</c> riep hem nog niet aan) — in
/// tegenstelling tot de teamcanonicalisatie, die in het origineel al best-effort is.
/// </para>
/// </summary>
internal static class PlannerMatchRepository
{
    /// <summary>
    /// Postgres-vertaling van het SQL Server-origineel (<c>PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync</c>).
    /// Zelfde teamalias-gebaseerde matching (#700) i.p.v. een rechtstreekse teamnaamvergelijking:
    /// de teamnaam die in <c>planner.geplandewedstrijden</c> staat en de teamnaam die in
    /// <c>his.matches</c> binnenkomt gebruiken verschillende schrijfwijzen, dus beide kanten worden
    /// via gevalideerde aliassen naar hetzelfde team herleid.
    /// <para>
    /// <c>UPPER(...)</c>-vergelijkingen op de teamalias-tekst — zelfde reden als #820's andere
    /// teamresolutie-fixes: Postgres' default-collatie is case-sensitive, in tegenstelling tot SQL
    /// Server's <c>Latin1_General_CI_AS</c>.
    /// </para>
    /// <para>
    /// Bewust ONGUARD (geen try/catch) — zelfde als het origineel: een fout hier hoort de hele
    /// sync te laten falen, in tegenstelling tot de (wél best-effort) teamcanonicalisatie.
    /// </para>
    /// </summary>
    internal static async Task MarkeerVervallenGeplandeWedstrijdenAsync(
        string connectionString, string clubCode, ILogger log)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        string accommodatie;
        try
        {
            accommodatie = await PostgresClubScope.RequireAccommodatieAsync(conn, clubCode);
        }
        catch (InvalidOperationException)
        {
            log.LogWarning(
                "Instelling 'accommodatie' niet geconfigureerd — MarkeerVervallenGeplandeWedstrijden overgeslagen. " +
                "Stel de accommodatienaam in via Admin GUI → Instellingen.");
            return;
        }

        await using var cmd = new NpgsqlCommand($@"
            UPDATE planner.geplandewedstrijden gw
            SET isvervallen = TRUE,
                sportlinkwedstrijdcode = m.wedstrijdcode,
                mta_modified = NOW()
            FROM his.matches m
            WHERE m.kaledatum::date = gw.datum
              AND {PostgresClubScope.HisFilter("m")}
              AND EXISTS (
                  SELECT 1
                  FROM public.teamaliassen amatch
                  INNER JOIN public.teamaliassen aplanner
                      ON aplanner.teamid = amatch.teamid
                     AND aplanner.clubcode = amatch.clubcode
                     AND aplanner.status = 'validated'
                     AND UPPER(aplanner.ruwetekst) = UPPER(gw.teamnaam)
                  WHERE amatch.clubcode = {PostgresClubScope.ClubCodeParam}
                    AND amatch.status = 'validated'
                    AND UPPER(amatch.ruwetekst) = UPPER(m.teamnaam))
              AND gw.isvervallen = FALSE
              AND gw.status <> 'Geannuleerd'
              AND gw.clubcode = {PostgresClubScope.ClubCodeParam}
              AND m.accommodatie ILIKE @accommodatiepattern
        ", conn);
        cmd.Parameters.AddWithValue("accommodatiepattern", $"%{accommodatie}%");
        PostgresClubScope.AddHisParams(cmd, clubCode);
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows > 0)
            log.LogInformation("Post-sync: {Count} geplande wedstrijd(en) als vervallen gemarkeerd", rows);
    }
}
