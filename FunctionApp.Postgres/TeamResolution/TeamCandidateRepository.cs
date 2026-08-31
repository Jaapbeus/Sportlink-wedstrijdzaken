using Npgsql;
using Planner.Shared;

namespace FunctionApp.Postgres.TeamResolution;

public sealed record TeamCandidate(int TeamId, string Teamnaam, string? LeeftijdsCategorie);

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp/TeamResolution/TeamCandidateRepository.cs</c> (#889), tegen
/// <c>public.teams</c>/<c>public.teamaliassen</c> (#887). Alle query's blijven hard gescoped op
/// clubcode, zelfde patroon als het origineel.
/// </summary>
internal sealed class TeamCandidateRepository(string connectionString)
{
    internal async Task<TeamCandidate?> FindValidatedAliasAsync(
        string clubCode, string ruweTekst, string genormaliseerdeSleutel)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT t.teamid, t.teamnaam, t.leeftijdscategorie
            FROM public.teamaliassen a
            INNER JOIN public.teams t ON t.teamid = a.teamid
            WHERE a.clubcode = @clubcode
              AND a.status = 'validated'
              AND t.isactief = TRUE
              AND (a.ruwetekst = @ruwetekst OR a.ruwetekstgenormaliseerd = @sleutel)
            ORDER BY CASE WHEN a.ruwetekst = @ruwetekst THEN 0 ELSE 1 END
            LIMIT 1
        ", conn);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("ruwetekst", ruweTekst);
        cmd.Parameters.AddWithValue("sleutel", genormaliseerdeSleutel);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadCandidate(reader) : null;
    }

    internal async Task<TeamCandidate?> FindExactTeamAsync(string clubCode, string genormaliseerdeSleutel)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT teamid, teamnaam, leeftijdscategorie
            FROM public.teams
            WHERE clubcode = @clubcode
              AND teamnaamgenormaliseerd = @sleutel
              AND isactief = TRUE
            LIMIT 1
        ", conn);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("sleutel", genormaliseerdeSleutel);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadCandidate(reader) : null;
    }

    internal async Task<IReadOnlyList<TeamCandidate>> FindKandidatenAsync(string clubCode, TeamNaamComponenten componenten)
    {
        if (componenten.LeeftijdNummer is null || componenten.TeamNummer is null)
            return [];

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT teamid, teamnaam, leeftijdscategorie
            FROM public.teams
            WHERE clubcode = @clubcode
              AND isactief = TRUE
              AND leeftijdnummer = @leeftijd
              AND teamnummer = @teamnummer
        ", conn);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("leeftijd", componenten.LeeftijdNummer.Value);
        cmd.Parameters.AddWithValue("teamnummer", componenten.TeamNummer.Value);

        var resultaten = new List<TeamCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            resultaten.Add(ReadCandidate(reader));
        return resultaten;
    }

    internal async Task<bool> HeeftActieveTeamsAsync(string clubCode)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM public.teams WHERE clubcode = @clubcode AND isactief = TRUE LIMIT 1", conn);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static TeamCandidate ReadCandidate(NpgsqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2));
}
