using Database.Postgres;
using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Planner.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het gedrag van <see cref="PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync"/>
/// vast (#888/#890).
///
/// <para>
/// <b>Waarom juist deze methode als eerste dekking krijgt.</b> Tot deze testklasse had de volledige
/// Postgres-plannerlaag (~873 regels) géén enkele blijvende test. Deze methode is daarbinnen de
/// gevaarlijkste: hij is het enige geporte <b>schrijfpad</b>, hij draait onbewaakt mee aan het einde
/// van elke synchronisatie, en hij markeert rijen als vervallen. Te veel markeren laat geplande
/// wedstrijden stilzwijgend uit de planning verdwijnen; te weinig markeren laat vervallen
/// wedstrijden staan. Geen van beide levert een foutmelding op.
/// </para>
///
/// <para>
/// <b>De kern is de aliaskoppeling, niet de UPDATE.</b> Een geplande wedstrijd wordt aan een
/// gesynchroniseerde wedstrijd gekoppeld via <c>public.teamaliassen</c> — aan beide kanten, en
/// uitsluitend via aliassen met status <c>validated</c>. Dat is de harde regel uit #692: een
/// geleerde alias telt pas mee ná goedkeuring door een coördinator, anders zou een foutieve gok
/// zichzelf kunnen versterken. Die regel is hier gedragsmatig vastgelegd en niet alleen tekstueel.
/// </para>
///
/// <para>
/// <b>Procesbrede toestand.</b> <see cref="PostgresClubScope.Primary"/> leest <c>clubCode</c> uit
/// de statische cache van <see cref="PostgresAppSettings"/>, niet rechtstreeks uit de database.
/// Deze klasse zet die cache daarom expliciet en ruimt hem in <see cref="Dispose"/> weer op —
/// zonder dat faalt elke test op "Vereiste instelling 'clubcode' ontbreekt", ook al staat de rij
/// wél in <c>public.appsettings</c>. Testparallellisme staat projectbreed uit
/// (<c>AssemblyInfo.cs</c>), dus dit kan geen andere test beïnvloeden.
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class PlannerMatchRepositoryIntegrationTests : IDisposable
{
    public void Dispose() => PostgresAppSettings.ResetForTests();

    private const string Club = "testclub-planner";
    private const string Accommodatie = "Sportpark Zelftest";
    private const string TeamInMatches = "AllStars JO13 1";
    private const string TeamInPlanner = "JO13-1";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task GevalideerdeAliassenAanBeideKanten_MarkerenDeWedstrijdAlsVervallen()
    {
        await using var conn = await OpstellingAsync();
        await AliasAsync(conn, TeamInMatches, "validated");
        await AliasAsync(conn, TeamInPlanner, "validated");

        await PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync(
            ConnectionString, Club, NullLogger.Instance);

        var (vervallen, code) = await StandAsync(conn);
        vervallen.Should().BeTrue("beide kanten hebben een gevalideerde alias naar hetzelfde team");
        code.Should().Be(9100001, "de gekoppelde Sportlink-wedstrijdcode hoort te worden overgenomen");
    }

    [PostgresFact]
    public async Task AliasNogNietGevalideerd_MarkeertNiets()
    {
        await using var conn = await OpstellingAsync();
        // Exact dezelfde situatie als de geslaagde test hierboven, met één verschil: de alias aan
        // de wedstrijdkant staat nog op 'pending'. Dat is de #692-regel — een niet-goedgekeurde
        // alias mag geen enkel gevolg hebben.
        await AliasAsync(conn, TeamInMatches, "pending");
        await AliasAsync(conn, TeamInPlanner, "validated");

        await PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync(
            ConnectionString, Club, NullLogger.Instance);

        var (vervallen, _) = await StandAsync(conn);
        vervallen.Should().BeFalse("een alias met status 'pending' telt niet mee (#692)");
    }

    [PostgresFact]
    public async Task AfwijkendeHoofdlettering_WordtTochGekoppeld()
    {
        await using var conn = await OpstellingAsync();
        // De UPPER()-wrapping aan beide kanten (#820) moet dit opvangen. Postgres' standaardcollatie
        // is hoofdlettergevoelig, dus zonder die wrapping zou dit stilzwijgend niets koppelen.
        await AliasAsync(conn, TeamInMatches.ToLowerInvariant(), "validated");
        await AliasAsync(conn, TeamInPlanner.ToLowerInvariant(), "validated");

        await PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync(
            ConnectionString, Club, NullLogger.Instance);

        var (vervallen, _) = await StandAsync(conn);
        vervallen.Should().BeTrue("de koppeling vergelijkt via UPPER() en is dus hoofdletterongevoelig");
    }

    [PostgresFact]
    public async Task GeannuleerdeWedstrijd_BlijftOngemoeid()
    {
        await using var conn = await OpstellingAsync(status: "Geannuleerd");
        await AliasAsync(conn, TeamInMatches, "validated");
        await AliasAsync(conn, TeamInPlanner, "validated");

        await PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync(
            ConnectionString, Club, NullLogger.Instance);

        var (vervallen, _) = await StandAsync(conn);
        vervallen.Should().BeFalse("een geannuleerde wedstrijd is al afgehandeld en wordt overgeslagen");
    }

    [PostgresFact]
    public async Task AndereAccommodatie_MarkeertNiets()
    {
        await using var conn = await OpstellingAsync(accommodatieInMatch: "Sportpark Tegenstander");
        await AliasAsync(conn, TeamInMatches, "validated");
        await AliasAsync(conn, TeamInPlanner, "validated");

        await PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync(
            ConnectionString, Club, NullLogger.Instance);

        var (vervallen, _) = await StandAsync(conn);
        vervallen.Should().BeFalse("een uitwedstrijd staat op het complex van de tegenstander en telt niet mee");
    }

    [PostgresFact]
    public async Task AccommodatieNietGeconfigureerd_SlaatOverZonderFout()
    {
        await using var conn = await OpstellingAsync(accommodatieInstelling: null);
        await AliasAsync(conn, TeamInMatches, "validated");
        await AliasAsync(conn, TeamInPlanner, "validated");

        var act = async () => await PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync(
            ConnectionString, Club, NullLogger.Instance);

        await act.Should().NotThrowAsync("een ontbrekende instelling is een configuratiekwestie, geen crash");
        var (vervallen, _) = await StandAsync(conn);
        vervallen.Should().BeFalse("zonder accommodatie is er geen betrouwbare koppeling, dus wordt er niets gemarkeerd");
    }

    // ── opstelling ─────────────────────────────────────────────────────────

    /// <summary>
    /// Zet één geplande wedstrijd en één gesynchroniseerde wedstrijd klaar die bij elkaar horen,
    /// plus de bijbehorende club- en teamrijen. Alleen de aliassen ontbreken — die zet elke test
    /// zelf, want juist daar zit het gedrag dat getoetst wordt.
    /// </summary>
    private static async Task<NpgsqlConnection> OpstellingAsync(
        string status = "Te bevestigen",
        string accommodatieInMatch = Accommodatie,
        string? accommodatieInstelling = Accommodatie)
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(ConnectionString, KnownEntities.Teams, KnownEntities.Matches);

        // Zie de klasse-doc-comment: PostgresClubScope.Primary leest uit de statische cache, niet
        // uit de database. Zonder dit faalt alles op een ontbrekende 'clubcode'-instelling.
        PostgresAppSettings.SetForTests("clubCode", Club);

        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await ExecAsync(conn, $"DELETE FROM planner.geplandewedstrijden WHERE clubcode = '{Club}'");
        await ExecAsync(conn, $"DELETE FROM public.teamaliassen WHERE clubcode = '{Club}'");
        await ExecAsync(conn, $"DELETE FROM public.teams WHERE clubcode = '{Club}'");
        await ExecAsync(conn, $"DELETE FROM his.matches WHERE clubcode = '{Club}'");
        await ExecAsync(conn, $"DELETE FROM public.velden WHERE clubcode = '{Club}'");
        await ExecAsync(conn, $"DELETE FROM public.appsettings WHERE clubcode = '{Club}'");

        await using (var club = new NpgsqlCommand(
            @"INSERT INTO public.appsettings (clubcode, syncenabled, accommodatie)
              VALUES (@club, true, @acc)", conn))
        {
            club.Parameters.AddWithValue("club", Club);
            club.Parameters.AddWithValue("acc", (object?)accommodatieInstelling ?? DBNull.Value);
            await club.ExecuteNonQueryAsync();
        }

        // Veldnummer 401 — public.velden.veldnummer is een kale PK zonder ClubCode-scope (migratie
        // 001), dus niet de 1/2 die PlannerAvailabilityRepositoryIntegrationTests al gebruikt.
        // Nodig sinds fk_geplandewedstrijden_velden (migratie 011, #888 vervolg) — de
        // planner.geplandewedstrijden-rij hieronder verwijst naar veldnummer 401.
        await using (var veld = new NpgsqlCommand(
            @"INSERT INTO public.velden (veldnummer, veldnaam, actief, clubcode, veldtype, heeftkunstlicht)
              VALUES (401, 'Veld 1', true, @club, 'kunstgras', true)", conn))
        {
            veld.Parameters.AddWithValue("club", Club);
            await veld.ExecuteNonQueryAsync();
        }

        // Eén canoniek team waar beide aliaskanten naartoe wijzen.
        await using (var team = new NpgsqlCommand(
            @"INSERT INTO public.teams (clubcode, teamnaam, teamnaamgenormaliseerd, isactief)
              VALUES (@club, @naam, @genormaliseerd, TRUE)", conn))
        {
            team.Parameters.AddWithValue("club", Club);
            team.Parameters.AddWithValue("naam", TeamInMatches);
            team.Parameters.AddWithValue("genormaliseerd", "JO131");
            await team.ExecuteNonQueryAsync();
        }

        // De gesynchroniseerde wedstrijd van vandaag, op de eigen accommodatie.
        await using (var match = new NpgsqlCommand(
            @"INSERT INTO his.matches
                  (wedstrijdcode, kaledatum, teamnaam, accommodatie, clubcode, mta_inserted, mta_modified)
              VALUES (9100001, to_char(CURRENT_DATE, 'YYYY-MM-DD'), @teamnaam, @acc, @club, NOW(), NOW())", conn))
        {
            match.Parameters.AddWithValue("teamnaam", TeamInMatches);
            match.Parameters.AddWithValue("acc", accommodatieInMatch);
            match.Parameters.AddWithValue("club", Club);
            await match.ExecuteNonQueryAsync();
        }

        // De geplande wedstrijd op diezelfde dag, met de plannernotatie van de teamnaam.
        await using (var gepland = new NpgsqlCommand(
            @"INSERT INTO planner.geplandewedstrijden
                  (datum, aanvangstijd, eindtijd, veldnummer, teamnaam, status, isvervallen, clubcode)
              VALUES (CURRENT_DATE, '09:00', '10:00', 401, @teamnaam, @status, FALSE, @club)", conn))
        {
            gepland.Parameters.AddWithValue("teamnaam", TeamInPlanner);
            gepland.Parameters.AddWithValue("status", status);
            gepland.Parameters.AddWithValue("club", Club);
            await gepland.ExecuteNonQueryAsync();
        }

        return conn;
    }

    private static async Task AliasAsync(NpgsqlConnection conn, string ruweTekst, string status)
    {
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO public.teamaliassen
                  (clubcode, ruwetekst, ruwetekstgenormaliseerd, teamid, bron, status)
              SELECT @club, @ruw, UPPER(@ruw), t.teamid, 'test', @status
              FROM public.teams t WHERE t.clubcode = @club LIMIT 1", conn);
        cmd.Parameters.AddWithValue("club", Club);
        cmd.Parameters.AddWithValue("ruw", ruweTekst);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<(bool Vervallen, long? Code)> StandAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT isvervallen, sportlinkwedstrijdcode FROM planner.geplandewedstrijden WHERE clubcode = '{Club}'",
            conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("de opstelling zet precies een geplande wedstrijd klaar");
        return (reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
