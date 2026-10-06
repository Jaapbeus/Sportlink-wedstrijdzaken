using FunctionApp.Postgres.TeamResolution;
using Npgsql;
using Planner.Shared;
using Planner.Shared.Leren;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/Repositories/SqlTeamAliasStore.cs</c> (#1568 deel C):
/// het aanmaken van een door een beheerder gevalideerde alias (bron <c>CoordinatorCorrectie</c>, direct
/// <c>validated</c>). De sleutel (<c>ruwetekstgenormaliseerd</c>) komt altijd uit
/// <c>TeamNaamNormalisatie</c> via de endpointkern — deze klasse berekent hem nooit zelf.
/// Alle sleutelvergelijkingen staan expliciet in <c>UPPER(...)</c> (#820), zodat de expressie-indexen
/// uit migratie 007/024 bruikbaar blijven.
/// </summary>
internal sealed class PostgresTeamAliasStore(string connectionString) : ITeamAliasStore
{
    private const string Bron = "CoordinatorCorrectie";

    public async Task<AliasAanmaakUitkomst> MaakAanAsync(AliasAanmaakOpdracht o)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

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
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // De unique-index vangt een gelijktijdige aanmaak; dat is een 409, geen 500.
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
    private async Task<IReadOnlyList<string>> DubbelzinnigeKandidatenAsync(AliasAanmaakOpdracht o)
    {
        var repository = new TeamCandidateRepository(connectionString);
        var exact = await repository.FindExactTeamAsync(o.ClubCode, o.Genormaliseerd) is not null;
        var componenten = TeamNaamNormalisatie.Parse(o.RuweTekst, o.ClubCode);
        if (exact || componenten is null) return [];
        var kandidaten = await repository.FindKandidatenAsync(o.ClubCode, componenten);
        return AliasDubbelzinnigheid.Kandidaten(exact, kandidaten.Select(k => k.Teamnaam).ToList());
    }

    /// <summary>Het aantal rijen dat herkoppelen raakt: de ene bestaande rij plus rijen met dezelfde sleutel die niet uit de Sportlink-sync komen.</summary>
    private static async Task<int> TelHerkoppelRijenAsync(NpgsqlConnection conn, AliasAanmaakOpdracht o, int id)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM public.teamaliassen
            WHERE clubcode = @cc AND (id = @id OR (bron <> 'Sync' AND (UPPER(ruwetekstgenormaliseerd) = UPPER(@sleutel)
                                                                    OR UPPER(ruwetekst) = UPPER(@ruw))))", conn);
        cmd.Parameters.AddWithValue("cc", o.ClubCode);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("sleutel", o.Genormaliseerd);
        cmd.Parameters.AddWithValue("ruw", o.RuweTekst);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private sealed record Bestaand(int Id, int TeamId, string? Teamnaam, string Status);

    private static async Task<string?> TeamnaamAsync(NpgsqlConnection conn, AliasAanmaakOpdracht o)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT teamnaam FROM public.teams WHERE teamid = @teamid AND clubcode = @cc AND isactief = TRUE LIMIT 1", conn);
        cmd.Parameters.AddWithValue("teamid", o.TeamId);
        cmd.Parameters.AddWithValue("cc", o.ClubCode);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task<Bestaand?> ZoekBestaandeAsync(NpgsqlConnection conn, AliasAanmaakOpdracht o)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT a.id, a.teamid, t.teamnaam, a.status
            FROM public.teamaliassen a
            LEFT JOIN public.teams t ON t.teamid = a.teamid AND t.clubcode = a.clubcode
            WHERE a.clubcode = @cc
              AND (UPPER(a.ruwetekstgenormaliseerd) = UPPER(@sleutel) OR UPPER(a.ruwetekst) = UPPER(@ruw))
            ORDER BY CASE WHEN a.status = 'validated' THEN 0 ELSE 1 END, a.id
            LIMIT 1", conn);
        cmd.Parameters.AddWithValue("cc", o.ClubCode);
        cmd.Parameters.AddWithValue("sleutel", o.Genormaliseerd);
        cmd.Parameters.AddWithValue("ruw", o.RuweTekst);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new Bestaand(r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3));
    }

    private static async Task<int> VoegToeAsync(NpgsqlConnection conn, AliasAanmaakOpdracht o)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.teamaliassen
                (clubcode, ruwetekst, ruwetekstgenormaliseerd, teamid, bron, status, aantalkeergebruikt,
                 aangemaaktdoor, aangemaaktdoornaam, aangemaaktop, herkomstverwerkingid, reden)
            VALUES (@cc, @ruw, @sleutel, @teamid, @bron, 'validated', 0, @door, @naam, NOW(), (SELECT id FROM planner.emailverwerking WHERE id = @verwerking::integer AND clubcode = @cc), @reden)
            RETURNING id", conn);
        cmd.Parameters.AddWithValue("cc", o.ClubCode);
        cmd.Parameters.AddWithValue("ruw", o.RuweTekst);
        cmd.Parameters.AddWithValue("sleutel", o.Genormaliseerd);
        cmd.Parameters.AddWithValue("teamid", o.TeamId);
        cmd.Parameters.AddWithValue("bron", Bron);
        VoegAuditToe(cmd, o);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Zet de rij (of bij herkoppelen alle rijen met dezelfde sleutel, behalve die uit de Sportlink-synchronisatie) op gevalideerd en naar het gekozen team.</summary>
    private static async Task ZetOpGevalideerdAsync(NpgsqlConnection conn, AliasAanmaakOpdracht o, int id, bool herkoppel)
    {
        await using var cmd = new NpgsqlCommand($@"
            UPDATE public.teamaliassen
            SET teamid = @teamid, status = 'validated', bron = CASE WHEN @herkoppel THEN @bron ELSE bron END,
                mta_modified = NOW(), beoordeelddoor = @door, beoordeelddoornaam = @naam, beoordeeldop = NOW(),
                herkomstverwerkingid = COALESCE((SELECT id FROM planner.emailverwerking WHERE id = @verwerking::integer AND clubcode = @cc), herkomstverwerkingid), reden = COALESCE(@reden::varchar, reden)
            WHERE clubcode = @cc AND (id = @id OR (@herkoppel AND bron <> 'Sync' AND (UPPER(ruwetekstgenormaliseerd) = UPPER(@sleutel)
                                                                                    OR UPPER(ruwetekst) = UPPER(@ruw))))", conn);
        cmd.Parameters.AddWithValue("cc", o.ClubCode);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("ruw", o.RuweTekst);
        cmd.Parameters.AddWithValue("sleutel", o.Genormaliseerd);
        cmd.Parameters.AddWithValue("teamid", o.TeamId);
        cmd.Parameters.AddWithValue("bron", Bron);
        cmd.Parameters.AddWithValue("herkoppel", herkoppel);
        VoegAuditToe(cmd, o);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>De herkomst-aanwijzing wordt via een subquery op de eigen club gelezen: een verwerking van een andere club of een onbestaand id wordt NULL, nooit een verwijzing.</summary>
    private static void VoegAuditToe(NpgsqlCommand cmd, AliasAanmaakOpdracht o)
    {
        cmd.Parameters.AddWithValue("door", o.Wie.DoorId);
        cmd.Parameters.AddWithValue("naam", (object?)o.Wie.DoorNaam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("verwerking", (object?)o.HerkomstVerwerkingId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reden", (object?)o.Reden ?? DBNull.Value);
    }
}
