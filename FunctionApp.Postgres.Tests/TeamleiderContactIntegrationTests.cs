using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Planner;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het gedrag vast van <see cref="AllstarsTestDataRepository.GetTeamleiderContactAsync"/>
/// (#1140, deelstuk 2 van #972) — de Postgres-vertaling van
/// <c>FunctionApp/Planner/Repositories/AllstarsTestDataRepository.GetTeamleiderContactAsync</c> die
/// <c>BerichtPipeline</c>'s <c>TeamContactOpvragen</c>-tak van een echt <c>coachGevonden</c> voorziet.
///
/// <para>
/// <b>Matching-sleutel bewust anders dan het SQL Server-origineel</b> — zie de klassekop van
/// <c>AllstarsTestDataRepository.cs</c>: hier via
/// <see cref="Planner.Shared.TeamNaamNormalisatie.NormaliseerVoorVergelijking"/> in plaats van een
/// tweede REPLACE-gebaseerde sleutel in SQL. <see cref="Gevonden_ViaKnvbNotatieZonderJPrefix"/>
/// bewijst dat beide notaties (lokaal mét J, KNVB zonder J) op dezelfde rij uitkomen.
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class TeamleiderContactIntegrationTests : IDisposable
{
    public void Dispose() => PostgresAppSettings.ResetForTests();

    // avg.teambegeleiding.clubcode/public.appsettings.clubcode zijn VARCHAR(20) — kort houden.
    private const string Club = "tlc-club";
    private const string AndereClub = "tlc-club-2";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task Gevonden_KiestTrainerBovenCoachEnTeamleider()
    {
        await using var conn = await OpstellingAsync(Club);
        await ZetBegeleiderAsync(conn, Club, "JO13-1", "Teamleider", "Frenkie", "teamleider@allstars-fc.test");
        await ZetBegeleiderAsync(conn, Club, "JO13-1", "Coach", "John", "coach@allstars-fc.test");
        await ZetBegeleiderAsync(conn, Club, "JO13-1", "Trainer", "Ruben", "trainer@allstars-fc.test");

        var contact = await AllstarsTestDataRepository.GetTeamleiderContactAsync(ConnectionString, "JO13-1", Club);

        contact.Should().NotBeNull();
        contact!.Naam.Should().Be("Ruben");
        contact.Emailadres.Should().Be("trainer@allstars-fc.test");
    }

    [PostgresFact]
    public async Task NietGevonden_GeeftNull()
    {
        await using var conn = await OpstellingAsync(Club);
        await ZetBegeleiderAsync(conn, Club, "JO13-1", "Trainer", "Ruben", "trainer@allstars-fc.test");

        var contact = await AllstarsTestDataRepository.GetTeamleiderContactAsync(ConnectionString, "JO99-9", Club);

        contact.Should().BeNull();
    }

    [PostgresFact]
    public async Task AndereClub_NietZichtbaar()
    {
        await using var conn = await OpstellingAsync(Club);
        await ZetBegeleiderAsync(conn, AndereClub, "JO13-1", "Trainer", "Ruben", "trainer@allstars-fc.test");

        var contact = await AllstarsTestDataRepository.GetTeamleiderContactAsync(ConnectionString, "JO13-1", Club);

        contact.Should().BeNull(
            "begeleidingscontacten zijn hard gescoped op ClubCode (AVG) — een andere club mag nooit meelekken");
    }

    /// <summary>
    /// Sportlink levert elk team in twee schrijfwijzen aan die geen gedeelde sleutel hebben: de
    /// lokale notatie ("JO13-1") en de KNVB-notatie zonder J, mét clubprefix ("tlc-club O13-1").
    /// avg.teambegeleiding bewaart de lokale vorm (zonder clubprefix) — deze test bewijst dat een
    /// vraag in de KNVB-vorm toch dezelfde begeleider vindt, via
    /// <see cref="Planner.Shared.TeamNaamNormalisatie.NormaliseerVoorVergelijking"/>.
    /// </summary>
    [PostgresFact]
    public async Task Gevonden_ViaKnvbNotatieZonderJPrefix()
    {
        await using var conn = await OpstellingAsync(Club);
        await ZetBegeleiderAsync(conn, Club, "JO13-1", "Trainer", "Ruben", "trainer@allstars-fc.test");

        // Club is de eigen ClubCode (PostgresAppSettings-cache = Club, via SetForTests hierboven);
        // TeamNaamNormalisatie strip die prefix vóór het vergelijken, zodat "TLC-CLUB O13-1"
        // (KNVB-vorm, zonder J) op dezelfde sleutel uitkomt als de lokaal opgeslagen "JO13-1".
        var contact = await AllstarsTestDataRepository.GetTeamleiderContactAsync(
            ConnectionString, $"{Club} O13-1", Club);

        contact.Should().NotBeNull("de KNVB-notatie zonder J-prefix moet op hetzelfde team normaliseren als de lokale vorm");
        contact!.Emailadres.Should().Be("trainer@allstars-fc.test");
    }

    [PostgresFact]
    public async Task SluitMedischeRolUit()
    {
        await using var conn = await OpstellingAsync(Club);
        await ZetBegeleiderAsync(conn, Club, "JO13-1", "Medische verzorging", "Frenkie", "medisch@allstars-fc.test");
        await ZetBegeleiderAsync(conn, Club, "JO13-1", "Overig", "John", "overig@allstars-fc.test");

        var contact = await AllstarsTestDataRepository.GetTeamleiderContactAsync(ConnectionString, "JO13-1", Club);

        contact.Should().NotBeNull();
        contact!.Emailadres.Should().Be("overig@allstars-fc.test",
            "de Medische-rol mag nooit als teamcontact worden aangeboden");
    }

    [PostgresFact]
    public async Task SlaatRijenZonderEmailadresOver()
    {
        await using var conn = await OpstellingAsync(Club);
        await ZetBegeleiderAsync(conn, Club, "JO13-1", "Trainer", "Ruben", null);
        await ZetBegeleiderAsync(conn, Club, "JO13-1", "Coach", "John", "coach@allstars-fc.test");

        var contact = await AllstarsTestDataRepository.GetTeamleiderContactAsync(ConnectionString, "JO13-1", Club);

        contact.Should().NotBeNull();
        contact!.Emailadres.Should().Be("coach@allstars-fc.test",
            "een trainer zonder e-mailadres kan de vraag niet ontvangen");
    }

    // ── opstelling ─────────────────────────────────────────────────────────

    private static async Task<NpgsqlConnection> OpstellingAsync(string primaireClub)
    {
        PostgresAppSettings.SetForTests("clubCode", primaireClub);

        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM avg.teambegeleiding WHERE clubcode = @club OR clubcode = @andereclub", conn))
        {
            cmd.Parameters.AddWithValue("club", Club);
            cmd.Parameters.AddWithValue("andereclub", AndereClub);
            await cmd.ExecuteNonQueryAsync();
        }

        return conn;
    }

    /// <remarks>
    /// AVG: uitsluitend fictieve gegevens — <c>@allstars-fc.test</c> is de in CLAUDE.md vastgelegde
    /// AllStars FC-demodata-conventie (RFC 2606-gereserveerd TLD, kan nooit een echt adres zijn),
    /// voornamen zonder achternaam.
    /// </remarks>
    private static async Task ZetBegeleiderAsync(
        NpgsqlConnection conn, string clubCode, string team, string teamrol, string naam, string? emailadres)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO avg.teambegeleiding (team, naam, teamrol, emailadres, clubcode)
            VALUES (@team, @naam, @rol, @email, @club)
            """, conn);
        cmd.Parameters.AddWithValue("team", team);
        cmd.Parameters.AddWithValue("naam", naam);
        cmd.Parameters.AddWithValue("rol", teamrol);
        cmd.Parameters.AddWithValue("email", (object?)emailadres ?? DBNull.Value);
        cmd.Parameters.AddWithValue("club", clubCode);
        await cmd.ExecuteNonQueryAsync();
    }
}
