using Npgsql;
using Planner.Shared.Leren;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/Repositories/SqlOnbekendeTeamTekstStore.cs</c>
/// (#1568 deel C) — data-access voor <c>planner.onbekendeteamtekst</c>: de wachtrij met teamteksten die
/// de pipeline niet kon koppelen. Gebruikt door de pipeline (upsert) én door de beheer-endpoints.
/// Elke query is gescoped op clubcode.
/// </summary>
internal sealed class PostgresOnbekendeTeamTekstStore(string connectionString) : IOnbekendeTeamTekstStore
{
    public async Task VoegToeAsync(string clubCode, string genormaliseerd, string voorbeeld, int verwerkingId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // Een retry van dezelfde verwerking telt niet dubbel; een eerder afgehandelde regel gaat weer open
        // (de alias loste het dus niet op), een genegeerde regel blijft genegeerd.
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO planner.onbekendeteamtekst (clubcode, ruwetekstgenormaliseerd, voorbeeldtekst, laatsteverwerkingid)
            VALUES (@cc, @sleutel, @voorbeeld, @verwerking)
            ON CONFLICT (clubcode, ruwetekstgenormaliseerd) DO UPDATE SET
                aantal = CASE WHEN planner.onbekendeteamtekst.laatsteverwerkingid IS NOT DISTINCT FROM EXCLUDED.laatsteverwerkingid
                              THEN planner.onbekendeteamtekst.aantal ELSE planner.onbekendeteamtekst.aantal + 1 END,
                laatstgezien = NOW(),
                voorbeeldtekst = EXCLUDED.voorbeeldtekst,
                status = CASE WHEN planner.onbekendeteamtekst.status = 'afgehandeld' THEN 'open'
                              ELSE planner.onbekendeteamtekst.status END,
                laatsteverwerkingid = EXCLUDED.laatsteverwerkingid", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("sleutel", genormaliseerd);
        cmd.Parameters.AddWithValue("voorbeeld", voorbeeld);
        cmd.Parameters.AddWithValue("verwerking", verwerkingId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<OnbekendeTeamTekstRij>> LijstAsync(string clubCode, string? status, int limit)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($@"
            SELECT id, voorbeeldtekst, ruwetekstgenormaliseerd, aantal, eerstgezien, laatstgezien, laatsteverwerkingid, status
            FROM planner.onbekendeteamtekst
            WHERE clubcode = @cc {(status is null ? "" : "AND status = @status")}
            ORDER BY CASE WHEN status = 'open' THEN 0 ELSE 1 END, laatstgezien DESC
            LIMIT @limit", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("limit", limit);
        if (status is not null) cmd.Parameters.AddWithValue("status", status);
        await using var r = await cmd.ExecuteReaderAsync();
        var lijst = new List<OnbekendeTeamTekstRij>();
        while (await r.ReadAsync())
            lijst.Add(new OnbekendeTeamTekstRij(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                r.GetDateTime(4), r.GetDateTime(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetString(7)));
        return lijst;
    }

    public async Task<int> AantalOpenAsync(string clubCode)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM planner.onbekendeteamtekst WHERE clubcode = @cc AND status = 'open'", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<int> ZetStatusAsync(string clubCode, int id, string status)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE planner.onbekendeteamtekst SET status = @status WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> MarkeerAfgehandeldAsync(string clubCode, string genormaliseerd)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE planner.onbekendeteamtekst SET status = 'afgehandeld'
            WHERE clubcode = @cc AND ruwetekstgenormaliseerd = @sleutel AND status = 'open'", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("sleutel", genormaliseerd);
        return await cmd.ExecuteNonQueryAsync();
    }
}
