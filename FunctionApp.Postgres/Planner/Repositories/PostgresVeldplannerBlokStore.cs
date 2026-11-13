using Npgsql;
using Planner.Shared.Planning;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-opslag van de veldplanner-blokken (#1915, migratie 050): <c>public.sportlinkveldplannerblok</c>. De
/// vergelijkingslogica staat in <see cref="VeldplannerBlokSynchronisatie"/>; deze klasse voert alleen de query's uit.
/// </summary>
internal sealed class PostgresVeldplannerBlokStore : IVeldplannerBlokStore
{
    private readonly string _connectionString;

    public PostgresVeldplannerBlokStore(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyDictionary<string, BlokOpslagStand>> LeesDagAsync(string clubCode, DateOnly datum, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT publicmatchid, rijhash, externalmatchid FROM public.sportlinkveldplannerblok WHERE clubcode = @clubcode AND datum = @datum",
            connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        command.Parameters.AddWithValue("datum", datum);

        var resultaat = new Dictionary<string, BlokOpslagStand>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            resultaat[reader.GetString(0)] = new BlokOpslagStand(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
        return resultaat;
    }

    public async Task SchrijfAsync(string clubCode, DateOnly datum, IReadOnlyList<VeldplannerBlokRij> rijen,
        IReadOnlyList<string> verwijderSleutels, DateOnly bewaarVanaf, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var rij in rijen)
        {
            // Een al bekend wedstrijdnummer wordt nooit overschreven met "onbekend".
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO public.sportlinkveldplannerblok
                    (clubcode, publicmatchid, datum, label, veld, starttijd, veldafmeting, duurminuten, externalmatchid, rijhash, bijgewerktop)
                VALUES (@clubcode, @publicmatchid, @datum, @label, @veld, @starttijd, @veldafmeting, @duurminuten, @externalmatchid, @rijhash, now())
                ON CONFLICT (clubcode, publicmatchid) DO UPDATE SET
                    datum = EXCLUDED.datum, label = EXCLUDED.label, veld = EXCLUDED.veld, starttijd = EXCLUDED.starttijd,
                    veldafmeting = EXCLUDED.veldafmeting, duurminuten = EXCLUDED.duurminuten,
                    externalmatchid = COALESCE(EXCLUDED.externalmatchid, public.sportlinkveldplannerblok.externalmatchid),
                    rijhash = EXCLUDED.rijhash, bijgewerktop = now()
                """, connection, transaction);
            upsert.Parameters.AddWithValue("clubcode", rij.ClubCode);
            upsert.Parameters.AddWithValue("publicmatchid", rij.PublicMatchId);
            upsert.Parameters.AddWithValue("datum", rij.Datum);
            upsert.Parameters.AddWithValue("label", rij.Label);
            upsert.Parameters.AddWithValue("veld", rij.Veld);
            upsert.Parameters.AddWithValue("starttijd", rij.StartTijd);
            upsert.Parameters.AddWithValue("veldafmeting", rij.Veldafmeting);
            upsert.Parameters.AddWithValue("duurminuten", rij.DuurMinuten);
            upsert.Parameters.AddWithValue("externalmatchid", (object?)rij.ExternalMatchId ?? DBNull.Value);
            upsert.Parameters.AddWithValue("rijhash", rij.RijHash);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        if (verwijderSleutels.Count > 0)
        {
            await using var verwijder = new NpgsqlCommand(
                "DELETE FROM public.sportlinkveldplannerblok WHERE clubcode = @clubcode AND datum = @datum AND publicmatchid = ANY(@sleutels)",
                connection, transaction);
            verwijder.Parameters.AddWithValue("clubcode", clubCode);
            verwijder.Parameters.AddWithValue("datum", datum);
            verwijder.Parameters.AddWithValue("sleutels", verwijderSleutels.ToArray());
            await verwijder.ExecuteNonQueryAsync(ct);
        }

        await using var opruim = new NpgsqlCommand(
            "DELETE FROM public.sportlinkveldplannerblok WHERE clubcode = @clubcode AND datum < @vanaf", connection, transaction);
        opruim.Parameters.AddWithValue("clubcode", clubCode);
        opruim.Parameters.AddWithValue("vanaf", bewaarVanaf);
        await opruim.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
    }
}
