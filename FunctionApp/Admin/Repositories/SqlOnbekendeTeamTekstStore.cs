using Microsoft.Data.SqlClient;
using Planner.Shared.Leren;

namespace SportlinkFunction.Admin;

/// <summary>
/// SQL Server-tier van de wachtrij met onbekende teamteksten (#1568 deel C) — data-access voor
/// <c>planner.OnbekendeTeamTekst</c>. Postgres-tegenhanger:
/// <c>FunctionApp.Postgres/Admin/Repositories/PostgresOnbekendeTeamTekstStore.cs</c>. Elke query is gescoped op ClubCode.
/// </summary>
internal sealed class SqlOnbekendeTeamTekstStore(string connectionString) : IOnbekendeTeamTekstStore
{
    public async Task VoegToeAsync(string clubCode, string genormaliseerd, string voorbeeld, int verwerkingId)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(connectionString);
        // Een retry van dezelfde verwerking telt niet dubbel; een eerder afgehandelde regel gaat weer open
        // (de alias loste het dus niet op), een genegeerde regel blijft genegeerd.
        using var cmd = new SqlCommand(@"
            MERGE [planner].[OnbekendeTeamTekst] WITH (HOLDLOCK) AS doel
            USING (SELECT @Cc AS [ClubCode], @Sleutel AS [RuweTekstGenormaliseerd]) AS bron
                ON doel.[ClubCode] = bron.[ClubCode] AND doel.[RuweTekstGenormaliseerd] = bron.[RuweTekstGenormaliseerd]
            WHEN MATCHED THEN UPDATE SET
                [Aantal] = CASE WHEN doel.[LaatsteVerwerkingId] = @Verwerking THEN doel.[Aantal] ELSE doel.[Aantal] + 1 END,
                [LaatstGezien] = GETUTCDATE(),
                [VoorbeeldTekst] = @Voorbeeld,
                [Status] = CASE WHEN doel.[Status] = N'afgehandeld' THEN N'open' ELSE doel.[Status] END,
                [LaatsteVerwerkingId] = @Verwerking
            WHEN NOT MATCHED THEN INSERT ([ClubCode], [RuweTekstGenormaliseerd], [VoorbeeldTekst], [LaatsteVerwerkingId])
                VALUES (@Cc, @Sleutel, @Voorbeeld, @Verwerking);", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        cmd.Parameters.AddWithValue("@Sleutel", genormaliseerd);
        cmd.Parameters.AddWithValue("@Voorbeeld", voorbeeld);
        cmd.Parameters.AddWithValue("@Verwerking", verwerkingId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<OnbekendeTeamTekstRij>> LijstAsync(string clubCode, string? status, int limit)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(connectionString);
        using var cmd = new SqlCommand($@"
            SELECT TOP (@Limit) [Id], [VoorbeeldTekst], [RuweTekstGenormaliseerd], [Aantal], [EerstGezien], [LaatstGezien],
                   [LaatsteVerwerkingId], [Status]
            FROM [planner].[OnbekendeTeamTekst]
            WHERE [ClubCode] = @Cc {(status is null ? "" : "AND [Status] = @Status")}
            ORDER BY CASE WHEN [Status] = N'open' THEN 0 ELSE 1 END, [LaatstGezien] DESC", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        cmd.Parameters.AddWithValue("@Limit", limit);
        if (status is not null) cmd.Parameters.AddWithValue("@Status", status);
        using var r = await cmd.ExecuteReaderAsync();
        var lijst = new List<OnbekendeTeamTekstRij>();
        while (await r.ReadAsync())
            lijst.Add(new OnbekendeTeamTekstRij(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc), DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc),
                r.IsDBNull(6) ? null : r.GetInt32(6), r.GetString(7)));
        return lijst;
    }

    public async Task<int> AantalOpenAsync(string clubCode)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(connectionString);
        using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM [planner].[OnbekendeTeamTekst] WHERE [ClubCode] = @Cc AND [Status] = N'open'", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<int> ZetStatusAsync(string clubCode, int id, string status)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(connectionString);
        using var cmd = new SqlCommand(
            "UPDATE [planner].[OnbekendeTeamTekst] SET [Status] = @Status WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Status", status);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> MarkeerAfgehandeldAsync(string clubCode, string genormaliseerd)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(connectionString);
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[OnbekendeTeamTekst] SET [Status] = N'afgehandeld'
            WHERE [ClubCode] = @Cc AND [RuweTekstGenormaliseerd] = @Sleutel AND [Status] = N'open'", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        cmd.Parameters.AddWithValue("@Sleutel", genormaliseerd);
        return await cmd.ExecuteNonQueryAsync();
    }
}
