using Microsoft.Data.SqlClient;

namespace SportlinkFunction.TeamResolution;

/// <summary>
/// SQL-implementatie van <see cref="ITeamCandidateRepository"/> tegen <c>dbo.Teams</c>/
/// <c>dbo.TeamAliassen</c>. Alle query's zijn hard gescoped op ClubCode, zelfde patroon als
/// <c>PlannerMatchRepository</c> (#573).
/// </summary>
public sealed class TeamCandidateRepository : ITeamCandidateRepository
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    public async Task<TeamCandidate?> FindValidatedAliasAsync(
        string clubCode, string ruweTekst, string genormaliseerdeSleutel)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        // Een treffer op de exacte bronschrijfwijze weegt zwaarder dan een treffer op de
        // genormaliseerde vorm: die eerste komt rechtstreeks uit de Sportlink-data.
        using var cmd = new SqlCommand(@"
            SELECT TOP 1 t.[TeamId], t.[Teamnaam], t.[LeeftijdsCategorie]
            FROM [dbo].[TeamAliassen] a
            INNER JOIN [dbo].[Teams] t ON t.[TeamId] = a.[TeamId]
            WHERE a.[ClubCode] = @clubCode
              AND a.[Status] = 'validated'
              AND t.[IsActief] = 1
              AND (a.[RuweTekst] = @ruweTekst OR a.[RuweTekstGenormaliseerd] = @sleutel)
            ORDER BY CASE WHEN a.[RuweTekst] = @ruweTekst THEN 0 ELSE 1 END
        ", conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        cmd.Parameters.AddWithValue("@ruweTekst", ruweTekst);
        cmd.Parameters.AddWithValue("@sleutel", genormaliseerdeSleutel);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadCandidate(reader) : null;
    }

    public async Task<TeamCandidate?> FindExactTeamAsync(string clubCode, string genormaliseerdeSleutel)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT TOP 1 [TeamId], [Teamnaam], [LeeftijdsCategorie]
            FROM [dbo].[Teams]
            WHERE [ClubCode] = @clubCode
              AND [TeamnaamGenormaliseerd] = @sleutel
              AND [IsActief] = 1
        ", conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        cmd.Parameters.AddWithValue("@sleutel", genormaliseerdeSleutel);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadCandidate(reader) : null;
    }

    public async Task<IReadOnlyList<TeamCandidate>> FindKandidatenAsync(string clubCode, TeamNaamComponenten componenten)
    {
        // Zonder leeftijd EN teamnummer zou dit elk team van de club teruggeven.
        if (componenten.LeeftijdNummer is null || componenten.TeamNummer is null)
            return [];

        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT [TeamId], [Teamnaam], [LeeftijdsCategorie]
            FROM [dbo].[Teams]
            WHERE [ClubCode] = @clubCode
              AND [IsActief] = 1
              AND [LeeftijdNummer] = @leeftijd
              AND [TeamNummer] = @teamNummer
        ", conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        cmd.Parameters.AddWithValue("@leeftijd", componenten.LeeftijdNummer.Value);
        cmd.Parameters.AddWithValue("@teamNummer", componenten.TeamNummer.Value);

        var resultaten = new List<TeamCandidate>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            resultaten.Add(ReadCandidate(reader));
        return resultaten;
    }

    public async Task<bool> HeeftActieveTeamsAsync(string clubCode)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT TOP 1 1 FROM [dbo].[Teams] WHERE [ClubCode] = @clubCode AND [IsActief] = 1", conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static TeamCandidate ReadCandidate(SqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2));
}
