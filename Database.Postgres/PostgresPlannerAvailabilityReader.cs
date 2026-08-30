using Npgsql;
using Planner.Shared;

namespace Database.Postgres;

/// <summary>
/// Leest veldbezetting uit <see cref="PostgresPlannerViewGenerator"/> en voert de veldresolutie
/// uit die de view zelf bewust niet doet (#819) — Postgres-tier-tegenhanger van
/// <c>FunctionApp/Planner/Repositories/PlannerAvailabilityRepository.GetFieldOccupationsAsync</c>.
/// <para>
/// Voor "Competitie"-rijen (<c>veld_ruw</c> gevuld) laadt deze klasse eenmalig de actieve velden
/// van de club en roept <see cref="VeldResolver.Resolve(string?, IEnumerable{ValueTuple{string?, int}})"/>
/// aan — exact dezelfde implementatie als de SQL Server-tier. Een rij waarvan het veld niet
/// resolveert (<c>VeldNummer == 0</c>) wordt overgeslagen, analoog aan de SQL Server-view's
/// <c>WHERE v.VeldNummer IS NOT NULL</c>-filter (dat filter zat daar in SQL omdat de resolutie
/// daar ook in SQL gebeurde; hier gebeurt de resolutie in C#, dus verhuist ook het filter mee).
/// "Planner"-rijen hebben al een resolved <c>veldnummer</c> en slaan de resolutiestap over.
/// </para>
/// </summary>
public static class PostgresPlannerAvailabilityReader
{
    public static async Task<List<VeldBezettingRij>> GetFieldOccupationsAsync(
        NpgsqlConnection connection, DateOnly date, string clubCode, CancellationToken ct = default)
    {
        var velden = await LoadActieveVeldenAsync(connection, clubCode, ct);
        var results = new List<VeldBezettingRij>();

        await using var command = new NpgsqlCommand($"""
            SELECT datum, aanvangstijd, eindtijd, veld_ruw, veldnummer, velddeelgebruik,
                   leeftijdscategorie, teamnaam, wedstrijd, bron, clubcode, wedstrijdcode
            FROM {PostgresPlannerViewGenerator.ViewName}
            WHERE datum = @datum AND clubcode = @clubcode
            """, connection);
        command.Parameters.AddWithValue("datum", date.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("clubcode", clubCode);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var veldRuw = reader.IsDBNull(3) ? null : reader.GetString(3);
            var veldNummerKolom = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);

            int veldNummer;
            string? subpositie;
            if (veldNummerKolom.HasValue)
            {
                // "Planner"-rij: al resolved, geen matching nodig.
                veldNummer = veldNummerKolom.Value;
                subpositie = null;
            }
            else
            {
                // "Competitie"-rij: los op via dezelfde implementatie als de SQL Server-tier.
                (veldNummer, subpositie) = VeldResolver.Resolve(veldRuw, velden);
                if (veldNummer == 0) continue; // spiegelt SQL Server-view's WHERE v.VeldNummer IS NOT NULL
            }

            results.Add(new VeldBezettingRij(
                Datum: DateOnly.FromDateTime(reader.GetDateTime(0)),
                AanvangsTijd: TimeOnly.FromTimeSpan(reader.GetTimeSpan(1)),
                EindTijd: TimeOnly.FromDateTime(reader.GetDateTime(2)),
                VeldNummer: veldNummer,
                VeldSubpositie: subpositie,
                VeldDeelGebruik: reader.GetDecimal(5),
                LeeftijdsCategorie: reader.IsDBNull(6) ? null : reader.GetString(6),
                TeamNaam: reader.IsDBNull(7) ? null : reader.GetString(7),
                Wedstrijd: reader.IsDBNull(8) ? null : reader.GetString(8),
                Bron: reader.GetString(9),
                ClubCode: reader.GetString(10),
                Wedstrijdcode: reader.IsDBNull(11) ? null : reader.GetInt64(11)));
        }

        return results;
    }

    private static async Task<List<(string? VeldNaam, int VeldNummer)>> LoadActieveVeldenAsync(
        NpgsqlConnection connection, string clubCode, CancellationToken ct)
    {
        var velden = new List<(string?, int)>();
        await using var command = new NpgsqlCommand(
            "SELECT veldnaam, veldnummer FROM public.velden WHERE clubcode = @clubcode AND actief = true",
            connection);
        command.Parameters.AddWithValue("clubcode", clubCode);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            velden.Add((reader.GetString(0), reader.GetInt32(1)));

        return velden;
    }
}
