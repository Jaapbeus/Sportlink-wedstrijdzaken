using AwesomeAssertions;
using Database.Postgres;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Planner.Shared.Leren;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Bewijst tegen een echte Postgres het leren vanuit de trace (#1568 deel C): alias aanmaken (aanmaken,
/// herhalen, conflict, expliciet herkoppelen), de wachtrij met onbekende teamteksten, en dat een
/// admin-leermoment de retentie overleeft terwijl een reply-leermoment wordt opgeruimd. Vereist
/// <c>POSTGRES_TEST_CONNECTION_STRING</c> en een gemigreerde database (039); draait in CI, niet lokaal zonder container.
/// </summary>
public class LerenVanTraceIntegrationTests
{
    private const string ClubCode = "testclub-leren";
    private static readonly LerenAanroeper Wie = new("oid-test", "Testbeheerder");

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException($"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet.");

    // ── Alias ─────────────────────────────────────────────────────────────────────────────────

    private static AliasAanmaakOpdracht Alias(string ruw, int teamId, bool herkoppel = false) => new(
        ClubCode, ruw, global::Planner.Shared.TeamNaamNormalisatie.NormaliseerVoorVergelijking(ruw, ClubCode), teamId, herkoppel, Wie, 42, "uit trace");

    [PostgresFact]
    public async Task Alias_Aanmaken_IsDirectGevalideerd_MetAuditEnBronCoordinatorCorrectie()
    {
        await SchoonAsync();
        var team = await MaakTeamAsync("TESTCLUB O10-4", "JO10-4");
        var store = new PostgresTeamAliasStore(ConnectionString);

        var uitkomst = await store.MaakAanAsync(Alias("j10-04", team));

        uitkomst.Status.Should().Be(AliasAanmaakStatus.Aangemaakt);
        var rij = await LeesAliasAsync(uitkomst.Id!.Value);
        rij.Status.Should().Be("validated");
        rij.Bron.Should().Be("CoordinatorCorrectie");
        rij.AangemaaktDoor.Should().Be("oid-test");
        rij.AangemaaktDoorNaam.Should().Be("Testbeheerder");
        rij.AangemaaktOpGezet.Should().BeTrue();
        rij.HerkomstVerwerkingId.Should().Be(42);
    }

    [PostgresFact]
    public async Task Alias_Herhaald_IsBestaatAl_EnVoorAnderTeamEenConflictTotExpliciteHerkoppeling()
    {
        await SchoonAsync();
        var team1 = await MaakTeamAsync("TESTCLUB O10-4", "JO10-4");
        var team2 = await MaakTeamAsync("TESTCLUB O10-5", "JO10-5");
        var store = new PostgresTeamAliasStore(ConnectionString);
        var eerste = await store.MaakAanAsync(Alias("j10-04", team1));

        (await store.MaakAanAsync(Alias("J10-04", team1))).Status.Should().Be(AliasAanmaakStatus.BestaatAl);

        var conflict = await store.MaakAanAsync(Alias("j10-04", team2));
        conflict.Status.Should().Be(AliasAanmaakStatus.Conflict);
        conflict.BestaandTeamId.Should().Be(team1);
        (await LeesAliasAsync(eerste.Id!.Value)).TeamId.Should().Be(team1, "zonder expliciete herkoppeling blijft de alias staan");

        var herkoppeld = await store.MaakAanAsync(Alias("j10-04", team2, herkoppel: true));
        herkoppeld.Status.Should().Be(AliasAanmaakStatus.Herkoppeld);
        var na = await LeesAliasAsync(eerste.Id!.Value);
        na.TeamId.Should().Be(team2);
        na.BeoordeeldDoor.Should().Be("oid-test");
    }

    [PostgresFact]
    public async Task Alias_VoorOnbekendTeam_GeeftTeamOnbekend()
    {
        await SchoonAsync();

        (await new PostgresTeamAliasStore(ConnectionString).MaakAanAsync(Alias("j10-04", 2_000_000_000)))
            .Status.Should().Be(AliasAanmaakStatus.TeamOnbekend);
    }

    [PostgresFact]
    public async Task Alias_ZetStatus_LegtWieEnWanneerVast()
    {
        await SchoonAsync();
        var team = await MaakTeamAsync("TESTCLUB O10-4", "JO10-4");
        var uitkomst = await new PostgresTeamAliasStore(ConnectionString).MaakAanAsync(Alias("j10-04", team));

        var rijen = await AdminTeamAliassenRepository.ZetStatusAsync(uitkomst.Id!.Value, "rejected", ClubCode, Wie, ConnectionString);

        rijen.Should().Be(1);
        var rij = await LeesAliasAsync(uitkomst.Id!.Value);
        rij.Status.Should().Be("rejected");
        rij.BeoordeeldDoor.Should().Be("oid-test");
        rij.BeoordeeldOpGezet.Should().BeTrue();
    }

    // ── Wachtrij ──────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Wachtrij_Upsert_TeltPerVerwerking_HeropentAfgehandeld_EnLaatGenegeerdStaan()
    {
        await SchoonAsync();
        var store = new PostgresOnbekendeTeamTekstStore(ConnectionString);

        await store.VoegToeAsync(ClubCode, "J10-04", "j10-04", 1);
        await store.VoegToeAsync(ClubCode, "J10-04", "j10-04", 1);   // retry van dezelfde verwerking
        (await store.LijstAsync(ClubCode, null, 10)).Should().ContainSingle().Which.Aantal.Should().Be(1);

        await store.VoegToeAsync(ClubCode, "J10-04", "J10-04", 2);
        var regel = (await store.LijstAsync(ClubCode, "open", 10)).Single();
        regel.Aantal.Should().Be(2);
        regel.LaatsteVerwerkingId.Should().Be(2);

        (await store.MarkeerAfgehandeldAsync(ClubCode, "J10-04")).Should().Be(1);
        (await store.AantalOpenAsync(ClubCode)).Should().Be(0);

        await store.VoegToeAsync(ClubCode, "J10-04", "j10-04", 3);
        (await store.AantalOpenAsync(ClubCode)).Should().Be(1, "een eerder afgehandelde regel die terugkomt is niet echt opgelost");

        await store.ZetStatusAsync(ClubCode, regel.Id, "genegeerd");
        await store.VoegToeAsync(ClubCode, "J10-04", "j10-04", 4);
        (await store.LijstAsync(ClubCode, null, 10)).Single().Status.Should().Be("genegeerd");
    }

    [PostgresFact]
    public async Task Wachtrij_IsGescooptOpClub()
    {
        await SchoonAsync();
        var store = new PostgresOnbekendeTeamTekstStore(ConnectionString);
        await store.VoegToeAsync(ClubCode, "J10-04", "j10-04", 1);

        (await store.LijstAsync("andere-club", null, 10)).Should().BeEmpty();
        (await store.ZetStatusAsync("andere-club", 1, "genegeerd")).Should().Be(0);
    }

    // ── Admin-leermoment en retentie ──────────────────────────────────────────────────────────

    private static AdminLeermomentOpdracht Leermoment(int? verwerkingId = 1) =>
        new(ClubCode, "BeschikbaarheidCheck", "HerplanVerzoek", "iemand wil een wedstrijd verzetten", Wie, verwerkingId);

    [PostgresFact]
    public async Task AdminLeermoment_IsDirectGevalideerd_HeeftGeenReplyPaar_EnKomtAlsEersteInDeFewShots()
    {
        await SchoonAsync();
        await LegReplyLeermomentVastAsync(valideer: true);

        var id = await AdminLeermomentenRepository.MaakAdminLeermomentAsync(Leermoment(), ConnectionString);

        var rij = await LeesLeermomentAsync(id);
        (rij.Herkomst, rij.IsGevalideerd, rij.OrigineleVerwerkingId, rij.CorrectionVerwerkingId).Should().Be(("Admin", true, (int?)null, (int?)null));
        rij.AangemaaktDoor.Should().Be("oid-test");

        var voorbeelden = await LearningMomentRepository.HaalVoorbeeldenOpAsync(ConnectionString, ClubCode, NullLogger.Instance);
        voorbeelden.Should().HaveCount(2);
        voorbeelden[0].OrigineleSamenvatting.Should().Be("iemand wil een wedstrijd verzetten", "admin-voorbeelden gaan voor binnen de limiet");
    }

    [PostgresFact]
    public async Task Retentie_RuimtReplyOp_MaarLaatAdminLeermomentenPermanentEnOngeanonimiseerd()
    {
        await SchoonAsync();
        var replyId = await LegReplyLeermomentVastAsync(valideer: false);
        var adminOud = await AdminLeermomentenRepository.MaakAdminLeermomentAsync(Leermoment(), ConnectionString);
        var adminMiddel = await AdminLeermomentenRepository.MaakAdminLeermomentAsync(Leermoment(2), ConnectionString);
        await ExecAsync("UPDATE planner.classificatiecorrectie SET mta_inserted = NOW() - INTERVAL '100 days' WHERE id = ANY(@ids)",
            ("ids", new[] { replyId, adminOud }));
        await ExecAsync("UPDATE planner.classificatiecorrectie SET mta_inserted = NOW() - INTERVAL '45 days' WHERE id = @id", ("id", adminMiddel));
        await ExecAsync("UPDATE planner.emailverwerking SET mta_inserted = NOW() - INTERVAL '100 days' WHERE clubcode = @club", ("club", ClubCode));

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await PostgresCleanupProcedures.CleanupEmailVerwerkingAsync(conn);
        await PostgresCleanupProcedures.CleanupClassificatieCorrectieAsync(conn);

        (await TelLeermomentAsync(replyId)).Should().Be(0, "een reply-leermoment verdwijnt na 90 dagen of met zijn verwerking");
        (await TelLeermomentAsync(adminOud)).Should().Be(1, "een admin-leermoment is permanent, ook zonder bijbehorende verwerking");
        (await LeesLeermomentAsync(adminMiddel)).OrigineleSamenvatting.Should().NotBeNull("een admin-leermoment wordt nooit geanonimiseerd");
    }

    [PostgresFact]
    public async Task Retentie_VerwijdertVerlopenWachtrijregels_MaarLaatRecenteStaan()
    {
        await SchoonAsync();
        var store = new PostgresOnbekendeTeamTekstStore(ConnectionString);
        await store.VoegToeAsync(ClubCode, "OUD", "oud", 1);
        await store.VoegToeAsync(ClubCode, "NIEUW", "nieuw", 2);
        await ExecAsync("UPDATE planner.onbekendeteamtekst SET laatstgezien = NOW() - INTERVAL '100 days' WHERE ruwetekstgenormaliseerd = 'OUD' AND clubcode = @club", ("club", ClubCode));

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await PostgresCleanupProcedures.CleanupEmailVerwerkingAsync(conn);

        (await store.LijstAsync(ClubCode, null, 10)).Should().ContainSingle().Which.Genormaliseerd.Should().Be("NIEUW");
    }

    // ── Hulpmethoden ──────────────────────────────────────────────────────────────────────────

    private static async Task<int> LegReplyLeermomentVastAsync(bool valideer)
    {
        var origineel = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(ConnectionString, Bericht($"orig-{Guid.NewGuid():N}"), ClubCode);
        var correctie = await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(ConnectionString, Bericht($"corr-{Guid.NewGuid():N}"), ClubCode);
        await LearningMomentRepository.InsertClassificatieCorrectieAsync(ConnectionString, origineel, correctie,
            "BeschikbaarheidCheck", "HerplanVerzoek", "reply-samenvatting", "correctie-samenvatting", ClubCode);
        var id = (int)(await ScalarAsync("SELECT id FROM planner.classificatiecorrectie WHERE origineleverwerkingid = @id", ("id", origineel)))!;
        if (valideer) await ExecAsync("UPDATE planner.classificatiecorrectie SET isgevalideerd = TRUE WHERE id = @id", ("id", id));
        return id;
    }

    private static InkomendBericht Bericht(string messageId) => new()
    {
        MessageId = messageId,
        Afzender = "trainer@voorbeeld.nl",
        Onderwerp = "Verzoek",
        OntvangstDatum = DateTime.UtcNow,
        Body = "tekst",
    };

    private static async Task<int> MaakTeamAsync(string naam, string sleutel)
    {
        var id = await ScalarAsync(@"
            INSERT INTO public.teams (clubcode, teamnaam, teamnaamgenormaliseerd) VALUES (@club, @naam, @sleutel)
            RETURNING teamid", ("club", ClubCode), ("naam", naam), ("sleutel", sleutel));
        return (int)id!;
    }

    private sealed record AliasRij(int TeamId, string Status, string Bron, string? AangemaaktDoor, string? AangemaaktDoorNaam,
        bool AangemaaktOpGezet, int? HerkomstVerwerkingId, string? BeoordeeldDoor, bool BeoordeeldOpGezet);

    private static async Task<AliasRij> LeesAliasAsync(int id)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT teamid, status, bron, aangemaaktdoor, aangemaaktdoornaam, aangemaaktop IS NOT NULL, herkomstverwerkingid,
                   beoordeelddoor, beoordeeldop IS NOT NULL
            FROM public.teamaliassen WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        (await r.ReadAsync()).Should().BeTrue();
        return new AliasRij(r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetInt32(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8));
    }

    private sealed record LeermomentRij(string Herkomst, bool IsGevalideerd, int? OrigineleVerwerkingId, int? CorrectionVerwerkingId,
        string? OrigineleSamenvatting, string? AangemaaktDoor);

    private static async Task<LeermomentRij> LeesLeermomentAsync(int id)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT herkomst, isgevalideerd, origineleverwerkingid, correctionverwerkingid, originelesamenvatting, aangemaaktdoor
            FROM planner.classificatiecorrectie WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        (await r.ReadAsync()).Should().BeTrue();
        return new LeermomentRij(r.GetString(0), r.GetBoolean(1), r.IsDBNull(2) ? null : r.GetInt32(2),
            r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));
    }

    private static async Task<long> TelLeermomentAsync(int id)
        => (long)(await ScalarAsync("SELECT count(*) FROM planner.classificatiecorrectie WHERE id = @id", ("id", id)))!;

    private static async Task SchoonAsync()
    {
        await ExecAsync("DELETE FROM planner.classificatiecorrectie WHERE clubcode = @club", ("club", ClubCode));
        await ExecAsync("DELETE FROM planner.emailverwerking WHERE clubcode = @club", ("club", ClubCode));
        await ExecAsync("DELETE FROM planner.onbekendeteamtekst WHERE clubcode = @club", ("club", ClubCode));
        await ExecAsync("DELETE FROM public.teamaliassen WHERE clubcode = @club", ("club", ClubCode));
        await ExecAsync("DELETE FROM public.teams WHERE clubcode = @club", ("club", ClubCode));
    }

    private static async Task ExecAsync(string sql, params (string Naam, object Waarde)[] parameters)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string sql, params (string Naam, object Waarde)[] parameters)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }
}
