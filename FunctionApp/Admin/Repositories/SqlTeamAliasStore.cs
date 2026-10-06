using Microsoft.Data.SqlClient;
using Planner.Shared;
using Planner.Shared.Leren;
using SportlinkFunction.TeamResolution;

namespace SportlinkFunction.Admin;

/// <summary>
/// SQL Server-tier van het aanmaken van een door een beheerder gevalideerde alias (#1568 deel C): bron
/// <c>CoordinatorCorrectie</c>, direct <c>validated</c>. Postgres-tegenhanger:
/// <c>FunctionApp.Postgres/Admin/Repositories/PostgresTeamAliasStore.cs</c>. De sleutel
/// (<c>RuweTekstGenormaliseerd</c>) komt altijd uit <c>TeamNaamNormalisatie</c> via de endpointkern.
/// De <c>UPPER()</c>-vergelijkingen matchen de persisted computed columns uit #1280, dus de indexen blijven bruikbaar.
/// </summary>
internal sealed class SqlTeamAliasStore(string connectionString) : ITeamAliasStore
{
    private const string Bron = "CoordinatorCorrectie";

    public async Task<AliasAanmaakUitkomst> MaakAanAsync(AliasAanmaakOpdracht o)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(connectionString);

        var teamnaam = await TeamnaamAsync(conn, o);
        if (teamnaam is null) return new AliasAanmaakUitkomst(AliasAanmaakStatus.TeamOnbekend);

        var bestaand = await ZoekBestaandeAsync(conn, o);
        if (bestaand is null)
        {
            if (!o.BevestigDubbelzinnig)
            {
                var kandidaten = await DubbelzinnigeKandidatenAsync(o);
                if (kandidaten.Count > 0)
                    return new AliasAanmaakUitkomst(AliasAanmaakStatus.Dubbelzinnig, Kandidaten: kandidaten);
            }

            try
            {
                var id = await VoegToeAsync(conn, o);
                return new AliasAanmaakUitkomst(AliasAanmaakStatus.Aangemaakt, id, teamnaam);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // Unique-violation door een gelijktijdige aanmaak: een 409, geen 500.
                return new AliasAanmaakUitkomst(AliasAanmaakStatus.GelijktijdigAangemaakt);
            }
        }

        if (bestaand.TeamId == o.TeamId)
        {
            if (bestaand.Status != "validated") await ZetOpGevalideerdAsync(conn, o, bestaand.Id, herkoppel: false);
            return new AliasAanmaakUitkomst(AliasAanmaakStatus.BestaatAl, bestaand.Id, teamnaam);
        }

        var aantalRijen = await TelHerkoppelRijenAsync(conn, o, bestaand.Id);
        if (!o.Herkoppel)
            return new AliasAanmaakUitkomst(AliasAanmaakStatus.Conflict, bestaand.Id, null,
                bestaand.TeamId, bestaand.Teamnaam, bestaand.Status, AantalRijenGeraakt: aantalRijen);

        await ZetOpGevalideerdAsync(conn, o, bestaand.Id, herkoppel: true);
        return new AliasAanmaakUitkomst(AliasAanmaakStatus.Herkoppeld, bestaand.Id, teamnaam, AantalRijenGeraakt: aantalRijen);
    }

    /// <summary>De teams waar de tekst nu al bij past, in dezelfde volgorde als de resolver (exacte teamnaam wint, dan leeftijd+nummer).</summary>
    private static async Task<IReadOnlyList<string>> DubbelzinnigeKandidatenAsync(AliasAanmaakOpdracht o)
    {
        var repository = new TeamCandidateRepository();
        var exact = await repository.FindExactTeamAsync(o.ClubCode, o.Genormaliseerd) is not null;
        var componenten = TeamNaamNormalisatie.Parse(o.RuweTekst, o.ClubCode);
        if (exact || componenten is null) return [];
        var kandidaten = await repository.FindKandidatenAsync(o.ClubCode, componenten);
        return AliasDubbelzinnigheid.Kandidaten(exact, kandidaten.Select(k => k.Teamnaam).ToList());
    }

    /// <summary>Het aantal rijen dat herkoppelen raakt: de ene bestaande rij plus rijen met dezelfde sleutel die niet uit de Sportlink-sync komen.</summary>
    private static async Task<int> TelHerkoppelRijenAsync(SqlConnection conn, AliasAanmaakOpdracht o, int id)
    {
        using var cmd = new SqlCommand(@"
            SELECT COUNT(*) FROM [dbo].[TeamAliassen]
            WHERE [ClubCode] = @Cc AND ([Id] = @Id OR ([Bron] <> 'Sync' AND (UPPER([RuweTekstGenormaliseerd]) = UPPER(@Sleutel)
                                                                       OR UPPER([RuweTekst]) = UPPER(@Ruw))))", conn);
        cmd.Parameters.AddWithValue("@Cc", o.ClubCode);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Sleutel", o.Genormaliseerd);
        cmd.Parameters.AddWithValue("@Ruw", o.RuweTekst);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private sealed record Bestaand(int Id, int TeamId, string? Teamnaam, string Status);

    private static async Task<string?> TeamnaamAsync(SqlConnection conn, AliasAanmaakOpdracht o)
    {
        using var cmd = new SqlCommand(
            "SELECT TOP 1 [Teamnaam] FROM [dbo].[Teams] WHERE [TeamId] = @TeamId AND [ClubCode] = @Cc AND [IsActief] = 1", conn);
        cmd.Parameters.AddWithValue("@TeamId", o.TeamId);
        cmd.Parameters.AddWithValue("@Cc", o.ClubCode);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task<Bestaand?> ZoekBestaandeAsync(SqlConnection conn, AliasAanmaakOpdracht o)
    {
        using var cmd = new SqlCommand(@"
            SELECT TOP 1 a.[Id], a.[TeamId], t.[Teamnaam], a.[Status]
            FROM [dbo].[TeamAliassen] a
            LEFT JOIN [dbo].[Teams] t ON t.[TeamId] = a.[TeamId] AND t.[ClubCode] = a.[ClubCode]
            WHERE a.[ClubCode] = @Cc
              AND (UPPER(a.[RuweTekstGenormaliseerd]) = UPPER(@Sleutel) OR UPPER(a.[RuweTekst]) = UPPER(@Ruw))
            ORDER BY CASE WHEN a.[Status] = 'validated' THEN 0 ELSE 1 END, a.[Id]", conn);
        cmd.Parameters.AddWithValue("@Cc", o.ClubCode);
        cmd.Parameters.AddWithValue("@Sleutel", o.Genormaliseerd);
        cmd.Parameters.AddWithValue("@Ruw", o.RuweTekst);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new Bestaand(r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3));
    }

    private static async Task<int> VoegToeAsync(SqlConnection conn, AliasAanmaakOpdracht o)
    {
        using var cmd = new SqlCommand(@"
            INSERT INTO [dbo].[TeamAliassen]
                ([ClubCode], [RuweTekst], [RuweTekstGenormaliseerd], [TeamId], [Bron], [Status], [AantalKeerGebruikt],
                 [AangemaaktDoor], [AangemaaktDoorNaam], [AangemaaktOp], [HerkomstVerwerkingId], [Reden])
            OUTPUT INSERTED.[Id]
            VALUES (@Cc, @Ruw, @Sleutel, @TeamId, @Bron, 'validated', 0, @Door, @Naam, GETUTCDATE(), @Verwerking, @Reden)", conn);
        cmd.Parameters.AddWithValue("@Cc", o.ClubCode);
        cmd.Parameters.AddWithValue("@Ruw", o.RuweTekst);
        cmd.Parameters.AddWithValue("@Sleutel", o.Genormaliseerd);
        cmd.Parameters.AddWithValue("@TeamId", o.TeamId);
        cmd.Parameters.AddWithValue("@Bron", Bron);
        VoegAuditToe(cmd, o);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Zet de rij (of bij herkoppelen alle rijen met dezelfde sleutel, behalve die uit de Sportlink-synchronisatie) op gevalideerd en naar het gekozen team.</summary>
    private static async Task ZetOpGevalideerdAsync(SqlConnection conn, AliasAanmaakOpdracht o, int id, bool herkoppel)
    {
        using var cmd = new SqlCommand(@"
            UPDATE [dbo].[TeamAliassen]
            SET [TeamId] = @TeamId, [Status] = 'validated', [Bron] = CASE WHEN @Herkoppel = 1 THEN @Bron ELSE [Bron] END,
                [mta_modified] = GETUTCDATE(), [BeoordeeldDoor] = @Door, [BeoordeeldDoorNaam] = @Naam, [BeoordeeldOp] = GETUTCDATE(),
                [HerkomstVerwerkingId] = COALESCE(@Verwerking, [HerkomstVerwerkingId]), [Reden] = COALESCE(@Reden, [Reden])
            WHERE [ClubCode] = @Cc AND ([Id] = @Id OR (@Herkoppel = 1 AND [Bron] <> 'Sync' AND (UPPER([RuweTekstGenormaliseerd]) = UPPER(@Sleutel)
                                                                                           OR UPPER([RuweTekst]) = UPPER(@Ruw))))", conn);
        cmd.Parameters.AddWithValue("@Cc", o.ClubCode);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Ruw", o.RuweTekst);
        cmd.Parameters.AddWithValue("@Sleutel", o.Genormaliseerd);
        cmd.Parameters.AddWithValue("@TeamId", o.TeamId);
        cmd.Parameters.AddWithValue("@Bron", Bron);
        cmd.Parameters.AddWithValue("@Herkoppel", herkoppel);
        VoegAuditToe(cmd, o);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void VoegAuditToe(SqlCommand cmd, AliasAanmaakOpdracht o)
    {
        cmd.Parameters.AddWithValue("@Door", o.Wie.DoorId);
        cmd.Parameters.AddWithValue("@Naam", (object?)o.Wie.DoorNaam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Verwerking", (object?)o.HerkomstVerwerkingId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Reden", (object?)o.Reden ?? DBNull.Value);
    }
}
