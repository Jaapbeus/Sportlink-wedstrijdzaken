using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Admin;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt de begeleiderselectie van <c>AdminTeambegeleidingDoorsturen</c> vast (issue 888 vervolg,
/// §43) — het stuk met echte, Postgres-specifieke logica in dat endpoint.
///
/// <para>
/// <b>Waarom juist deze methode dekking krijgt en niet de hele handler.</b> De rest van
/// <c>Doorsturen</c> is validatie, een DI-lookup en een Graph-aanroep; die eerste twee zijn
/// rechttoe-rechtaan en de derde is per definitie niet lokaal te bewijzen (uitgaande integratie,
/// afgeschermd door <c>EgressGuard</c>). De rolvolgorde-query is wél logica die stil fout kan gaan:
/// hij bepaalt wie de vraag van een ouder krijgt.
/// </para>
///
/// <para>
/// <b>De ILIKE-regressie die deze tests bewaken.</b> Het SQL Server-origineel gebruikt
/// <c>LIKE '%Trainer%'</c>, wat daar hoofdletterongevoelig is door de
/// <c>Latin1_General_CI_AS</c>-collatie. Op Postgres is <c>LIKE</c> dat niet. De teamrol komt uit
/// een handmatig aangeleverde CSV, dus "trainer" in kleine letters komt voor — en viel dan
/// stilzwijgend in de ELSE-tak, waardoor niet de trainer maar een willekeurige andere begeleider
/// bovenaan kwam. Zelfde klasse fout als #820.
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class TeambegeleidingDoorsturenIntegrationTests
{
    private const string Club = "testclub-doorsturen";
    private const string Team = "T-doorsturen JO13-1";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task ZoekBegeleiderEmail_KiestDeTrainerBovenCoachEnTeamleider()
    {
        await using var conn = await OpstellingAsync();
        await ZetBegeleiderAsync(conn, "Teamleider", "teamleider@voorbeeld.nl");
        await ZetBegeleiderAsync(conn, "Coach", "coach@voorbeeld.nl");
        await ZetBegeleiderAsync(conn, "Trainer", "trainer@voorbeeld.nl");

        var email = await AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync(ConnectionString, Team, Club);

        email.Should().Be("trainer@voorbeeld.nl");
    }

    [PostgresFact]
    public async Task ZoekBegeleiderEmail_RolInKleineLetters_TeltNogSteedsAlsTrainer()
    {
        await using var conn = await OpstellingAsync();
        // Deze volgorde is bewust: zonder ILIKE valt "trainer" in de ELSE-tak (4) en wint de
        // teamleider (3) — precies de stille fout die deze test moet vangen.
        await ZetBegeleiderAsync(conn, "Teamleider", "teamleider@voorbeeld.nl");
        await ZetBegeleiderAsync(conn, "trainer", "trainer@voorbeeld.nl");

        var email = await AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync(ConnectionString, Team, Club);

        email.Should().Be("trainer@voorbeeld.nl",
            "LIKE is op Postgres hoofdlettergevoelig; de query hoort ILIKE te gebruiken");
    }

    [PostgresFact]
    public async Task ZoekBegeleiderEmail_SlaatRijenZonderEmailadresOver()
    {
        await using var conn = await OpstellingAsync();
        await ZetBegeleiderAsync(conn, "Trainer", null);
        await ZetBegeleiderAsync(conn, "Coach", "coach@voorbeeld.nl");

        var email = await AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync(ConnectionString, Team, Club);

        email.Should().Be("coach@voorbeeld.nl",
            "een trainer zonder e-mailadres kan de vraag niet ontvangen");
    }

    [PostgresFact]
    public async Task ZoekBegeleiderEmail_AndereClub_GeeftNull()
    {
        await using var conn = await OpstellingAsync();
        await ZetBegeleiderAsync(conn, "Trainer", "trainer@voorbeeld.nl");

        var email = await AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync(
            ConnectionString, Team, "een-andere-club");

        email.Should().BeNull("begeleidersgegevens zijn hard gescoped op ClubCode (AVG)");
    }

    [PostgresFact]
    public async Task ZoekBegeleiderEmail_OnbekendTeam_GeeftNull()
    {
        await using var conn = await OpstellingAsync();
        await ZetBegeleiderAsync(conn, "Trainer", "trainer@voorbeeld.nl");

        var email = await AdminTeambegeleidingFunction.ZoekBegeleiderEmailAsync(
            ConnectionString, "T-doorsturen JO99-9", Club);

        email.Should().BeNull();
    }

    // ── opstelling ─────────────────────────────────────────────────────────

    private static async Task<NpgsqlConnection> OpstellingAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM avg.teambegeleiding WHERE clubcode = @club", conn))
        {
            cmd.Parameters.AddWithValue("club", Club);
            await cmd.ExecuteNonQueryAsync();
        }
        return conn;
    }

    /// <remarks>
    /// AVG: uitsluitend fictieve gegevens — <c>voorbeeld.nl</c> is de in CLAUDE.md vastgelegde
    /// placeholder en bestaat niet publiek.
    /// </remarks>
    private static async Task ZetBegeleiderAsync(NpgsqlConnection conn, string teamrol, string? emailadres)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO avg.teambegeleiding (team, naam, teamrol, emailadres, clubcode)
            VALUES (@team, @naam, @rol, @email, @club)
            """, conn);
        cmd.Parameters.AddWithValue("team", Team);
        cmd.Parameters.AddWithValue("naam", $"Jan de Vries ({teamrol})");
        cmd.Parameters.AddWithValue("rol", teamrol);
        cmd.Parameters.AddWithValue("email", (object?)emailadres ?? DBNull.Value);
        cmd.Parameters.AddWithValue("club", Club);
        await cmd.ExecuteNonQueryAsync();
    }
}
