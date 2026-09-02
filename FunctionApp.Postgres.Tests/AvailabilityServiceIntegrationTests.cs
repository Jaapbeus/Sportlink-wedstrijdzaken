using Database.Postgres;
using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Planner.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Planner.Shared;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het gedrag van <see cref="AvailabilityService"/> en <see cref="RescheduleService"/> vast
/// (issue 888 vervolg, §41) — samen met <see cref="PlannerSettingsRepository.GetSunsetAsync"/>/
/// <see cref="PlannerSettingsRepository.PopulateSunsetTableAsync"/> en
/// <see cref="PlannerMatchRepository.GetTeamMatchesOnDateAsync"/>, de laatste stukjes
/// repositorylaag die deze twee services nodig hadden.
///
/// <para>
/// <b>Zaterdag, niet doordeweeks</b> — <c>public.veldbeschikbaarheid</c> is hier alleen voor
/// dagvanweek 6 (zaterdag) geseed, zodat <c>CheckAvailabilityAsync</c>'s tests een simpele,
/// voorspelbare beschikbaarheid hebben. <c>CheckDoordeweeksBeschikbaarAsync_...</c> seedt zijn eigen
/// doordeweekse beschikbaarheid apart.
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class AvailabilityServiceIntegrationTests : IDisposable
{
    public void Dispose() => PostgresAppSettings.ResetForTests();

    private const string Club = "testclub-availsvc";
    private static readonly DateOnly Zaterdag = new(2026, 9, 5); // ver genoeg in de toekomst t.o.v. "vandaag" tijdens CI-runs

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task CheckAvailabilityAsync_VindtSlotOpVoorkeurstijd()
    {
        await using var conn = await OpstellingAsync();

        var request = new CheckAvailabilityRequest
        {
            Datum = Zaterdag.ToString("yyyy-MM-dd"),
            AanvangsTijd = "10:00",
            LeeftijdsCategorie = "JO13"
        };
        var response = await AvailabilityService.CheckAvailabilityAsync(ConnectionString, request, NullLogger.Instance, Club);

        response.Beschikbaar.Should().BeTrue(response.Reden);
        response.Toewijzing.Should().NotBeNull();
        response.Toewijzing!.AanvangsTijd.Should().Be("10:00");
        response.Toewijzing.EindTijd.Should().Be("11:00", "Speeltijd JO13 levert 60 minuten wedstrijdduur");
    }

    [PostgresFact]
    public async Task CheckAvailabilityAsync_TeamHeeftAlWedstrijd_GeeftTeamConflict()
    {
        await using var conn = await OpstellingAsync();
        const string team = "T-availsvc JO13-1";
        await ExecAsync(conn, @"
            INSERT INTO his.matches (wedstrijdcode, kaledatum, aanvangstijd, veld, teamnaam, wedstrijd, accommodatie, status, clubcode, mta_inserted, mta_modified)
            VALUES (9400001, '2026-09-05', '09:00', 'veld 1', @team, @team || ' - Bestaande tegenstander', 'Sportpark Testclub', 'Te spelen', @club, NOW(), NOW())",
            ("team", team), ("club", Club));

        var request = new CheckAvailabilityRequest
        {
            Datum = Zaterdag.ToString("yyyy-MM-dd"),
            AanvangsTijd = "14:00",
            LeeftijdsCategorie = "JO13",
            TeamNaam = team
        };
        var response = await AvailabilityService.CheckAvailabilityAsync(ConnectionString, request, NullLogger.Instance, Club);

        response.Beschikbaar.Should().BeFalse();
        response.TeamConflict.Should().NotBeNull();
        response.TeamConflict!.AanvangsTijd.Should().Be("09:00");
        response.Reden.Should().Contain(team);

        response.Waarschuwingen.Should().NotContain(w => w.Contains("NIET gecontroleerd"),
            "dit team staat wél in de canonieke lijst; de niet-gecontroleerd-waarschuwing uit #945 hoort "
            + "hier juist te ontbreken — anders zou die waarschuwing altijd meekomen en niets onderscheiden");
    }

    /// <summary>
    /// Het team staat NIET in de canonieke teamlijst, maar heeft die dag wél een wedstrijd in
    /// <c>his.matches</c> (#945).
    ///
    /// <para>
    /// <b>Het gedrag dat deze test afgrendelt.</b> De teamconflictcontrole zoekt het team eerst op in
    /// <c>public.teams</c>/<c>public.teamaliassen</c>. Levert dat niets op, dan is er niets om mee te
    /// vergelijken — maar tot #945 gaf dat een lege lijst terug, precies zoals "dit team heeft die dag
    /// geen wedstrijd". Het antwoord luidde dan <c>beschikbaar</c>, zonder waarschuwing en zonder
    /// logregel, terwijl het team op hetzelfde moment al ingepland stond. De planner kon daarop een
    /// dubbele boeking maken.
    /// </para>
    ///
    /// <para>
    /// Let op het contrast met <see cref="CheckAvailabilityAsync_TeamHeeftAlWedstrijd_GeeftTeamConflict"/>
    /// hierboven: die gebruikt hetzelfde scenario mét een canonieke rij en moet een echt conflict
    /// melden. Samen scheiden de twee tests "niet gecontroleerd" van "gecontroleerd, geen conflict" —
    /// los van elkaar bewijst geen van beide dat onderscheid.
    /// </para>
    /// </summary>
    [PostgresFact]
    public async Task CheckAvailabilityAsync_TeamNietInTeamlijst_WaarschuwtDatErNietsIsGecontroleerd()
    {
        await using var conn = await OpstellingAsync();

        // Bewust GEEN rij in public.teams/public.teamaliassen voor deze naam.
        const string team = "T-availsvc JO99-9";
        await ExecAsync(conn, @"
            INSERT INTO his.matches (wedstrijdcode, kaledatum, aanvangstijd, veld, teamnaam, wedstrijd, accommodatie, status, clubcode, mta_inserted, mta_modified)
            VALUES (9400009, '2026-09-05', '09:00', 'veld 1', @team, @team || ' - Bestaande tegenstander', 'Sportpark Testclub', 'Te spelen', @club, NOW(), NOW())",
            ("team", team), ("club", Club));

        var request = new CheckAvailabilityRequest
        {
            Datum = Zaterdag.ToString("yyyy-MM-dd"),
            AanvangsTijd = "14:00",
            LeeftijdsCategorie = "JO13",
            TeamNaam = team
        };
        var response = await AvailabilityService.CheckAvailabilityAsync(ConnectionString, request, NullLogger.Instance, Club);

        response.Waarschuwingen.Should().Contain(w => w.Contains("NIET gecontroleerd"),
            "een niet-herleidbaar team betekent dat de conflictcontrole is overgeslagen; dat moet in het "
            + "antwoord staan en mag niet als 'geen conflict' lezen (#945)");

        response.TeamConflict.Should().BeNull(
            "er is geen conflict vastgesteld — de controle kon juist niet worden uitgevoerd; een verzonnen "
            + "conflict zou net zo misleidend zijn als een verzwegen waarschuwing");
    }

    [PostgresFact]
    public async Task CheckAvailabilityAsync_OnbekendeLeeftijdscategorie_GeeftReden()
    {
        await using var conn = await OpstellingAsync();
        _ = conn;

        var request = new CheckAvailabilityRequest
        {
            Datum = Zaterdag.ToString("yyyy-MM-dd"),
            LeeftijdsCategorie = "OnbekendeCategorieX"
        };
        var response = await AvailabilityService.CheckAvailabilityAsync(ConnectionString, request, NullLogger.Instance, Club);

        response.Beschikbaar.Should().BeFalse();
        response.Reden.Should().Contain("Onbekende leeftijdscategorie");
    }

    [PostgresFact]
    public async Task CheckAvailabilityAsync_ZonderAanvangsTijd_GeeftBeschikbareVensters()
    {
        await using var conn = await OpstellingAsync();
        _ = conn;

        var request = new CheckAvailabilityRequest
        {
            Datum = Zaterdag.ToString("yyyy-MM-dd"),
            LeeftijdsCategorie = "JO13"
        };
        var response = await AvailabilityService.CheckAvailabilityAsync(ConnectionString, request, NullLogger.Instance, Club);

        response.BeschikbareVensters.Should().NotBeNullOrEmpty();
        response.BeschikbareVensters!.Should().Contain(v => v.VeldNummer == 501 || v.VeldNummer == 502);
    }

    [PostgresFact]
    public async Task PopulateSunsetTableAsync_EnGetSunsetAsync_RoundTrip()
    {
        await using var conn = await OpstellingAsync();
        _ = conn;

        await PlannerSettingsRepository.PopulateSunsetTableAsync(ConnectionString, Zaterdag, Zaterdag);
        var sunset = await PlannerSettingsRepository.GetSunsetAsync(ConnectionString, Zaterdag);

        sunset.Should().NotBeNull();
        // September in Nederland: zonsondergang ergens tussen 19:00 en 21:00 lokale tijd.
        sunset!.Value.Should().BeAfter(new TimeOnly(18, 0)).And.BeBefore(new TimeOnly(21, 30));
    }

    [PostgresFact]
    public async Task CheckRescheduleAvailabilityAsync_VindtAlternatievenExclusiefEigenWedstrijd()
    {
        await using var conn = await OpstellingAsync();
        const string team = "T-availsvc JO13-1";
        await ExecAsync(conn, @"
            INSERT INTO his.matches (wedstrijdcode, kaledatum, aanvangstijd, veld, teamnaam, wedstrijd, accommodatie, status, clubcode, mta_inserted, mta_modified)
            VALUES (9400002, '2026-09-05', '10:00', 'veld 1', @team, @team || ' - Tegenstander', 'Sportpark Testclub', 'Te spelen', @club, NOW(), NOW())",
            ("team", team), ("club", Club));

        var request = new HerplanCheckRequest { Wedstrijdcode = 9400002 };
        var response = await RescheduleService.CheckRescheduleAvailabilityAsync(ConnectionString, request, NullLogger.Instance, Club);

        response.Beschikbaar.Should().BeTrue(response.Reden);
        response.Alternatieven.Should().NotBeEmpty();
        // De eigen wedstrijd (veld 1, 10:00) mag niet als "alternatief" voor zichzelf terugkomen.
        response.Alternatieven.Should().NotContain(a => a.VeldNummer == 1 && a.AanvangsTijd == "10:00");
    }

    [PostgresFact]
    public async Task CheckRescheduleAvailabilityAsync_OnbekendeWedstrijdcode_GeeftReden()
    {
        await using var conn = await OpstellingAsync();
        _ = conn;

        var request = new HerplanCheckRequest { Wedstrijdcode = 9999999 };
        var response = await RescheduleService.CheckRescheduleAvailabilityAsync(ConnectionString, request, NullLogger.Instance, Club);

        response.Beschikbaar.Should().BeFalse();
        response.Reden.Should().Contain("niet gevonden");
    }

    // ── opstelling ─────────────────────────────────────────────────────────

    private static async Task<NpgsqlConnection> OpstellingAsync()
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(ConnectionString, KnownEntities.Teams, KnownEntities.Matches);
        PostgresAppSettings.SetForTests("clubCode", Club);

        await using (var setupConn = new NpgsqlConnection(ConnectionString))
        {
            await setupConn.OpenAsync();
            await using var view = new NpgsqlCommand(PostgresPlannerViewGenerator.CreateView, setupConn);
            await view.ExecuteNonQueryAsync();
        }

        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var sql in new[]
        {
            "DELETE FROM planner.geplandewedstrijden WHERE clubcode = @club",
            "DELETE FROM his.matches WHERE clubcode = @club",
            "DELETE FROM his.teams WHERE clubcode = @club",
            "DELETE FROM public.teamaliassen WHERE clubcode = @club",
            "DELETE FROM public.teams WHERE clubcode = @club",
            "DELETE FROM public.teamregels WHERE clubcode = @club",
            "DELETE FROM public.veldtraining WHERE clubcode = @club",
            "DELETE FROM public.veldbeschikbaarheid WHERE clubcode = @club",
            "DELETE FROM public.velden WHERE clubcode = @club",
            "DELETE FROM public.speeltijden WHERE clubcode = @club",
            "DELETE FROM public.appsettings WHERE clubcode = @club",
        })
            await ExecAsync(conn, sql, ("club", Club));

        // Veldnummers 501/502 — public.velden.veldnummer is een kale PK zonder ClubCode-scope
        // (migratie 001); dit is de vierde testklasse die eigen velden seedt, dus een eigen reeks.
        await ExecAsync(conn, @"
            INSERT INTO public.appsettings (clubcode, syncenabled, accommodatie, sportlinkapiurl, sportlinkclientid)
            VALUES (@club, true, 'Sportpark Testclub', 'x', 'x')", ("club", Club));
        await ExecAsync(conn, @"
            INSERT INTO public.velden (veldnummer, veldnaam, actief, clubcode, veldtype, heeftkunstlicht)
            VALUES (501, 'Veld 1', true, @club, 'kunstgras', true), (502, 'Veld 2', true, @club, 'natuurgras', false)",
            ("club", Club));
        await ExecAsync(conn, @"
            INSERT INTO public.veldbeschikbaarheid (veldnummer, dagvanweek, beschikbaarvanaf, beschikbaartot, gebruikzonsondergang, clubcode, periodeid)
            VALUES (501, 6, '08:00', '20:00', false, @club, NULL), (502, 6, '08:00', '20:00', false, @club, NULL)",
            ("club", Club));
        await ExecAsync(conn,
            "INSERT INTO public.speeltijden (leeftijd, veldafmeting, wedstrijdtotaal, clubcode) VALUES ('JO13', 1.00, 60, @club) ON CONFLICT DO NOTHING",
            ("club", Club));
        // his.teams-rij voor T-availsvc JO13-1 — zonder deze rij mist his.matches z'n
        // leeftijdscategorie-koppeling en gooit FindMatchByCodeAsync/GetTeamMatchesOnDateAsync
        // "Speelduur niet geconfigureerd", ook als er wél een public.speeltijden-rij voor JO13 is.
        // Businesskey (teamcode, lokaleteamcode, poulecode) is géén ClubCode-gescoped uniqueindex —
        // 493 vermijdt de botsing met andere testklassen (393, 1) die xunit parallel/na elkaar draait.
        await ExecAsync(conn, @"
            INSERT INTO his.teams (teamcode, lokaleteamcode, poulecode, teamnaam, leeftijdscategorie, clubcode, mta_inserted, mta_modified)
            VALUES (493, 493, 493, 'T-availsvc JO13-1', 'Onder 13', @club, NOW(), NOW())",
            ("club", Club));
        // public.teams-rij — TeamSchrijfwijzenAsync (GetTeamMatchesOnDateAsync's teamresolutie)
        // herleidt via de canonieke teamlijst, niet via his.teams; zonder deze rij levert die
        // resolutie een lege schrijfwijzenlijst op en wordt het conflict stilzwijgend gemist.
        await ExecAsync(conn, @"
            INSERT INTO public.teams (clubcode, teamnaam, teamnaamgenormaliseerd, isactief)
            VALUES (@club, 'T-availsvc JO13-1', @sleutel, true)",
            ("club", Club), ("sleutel", TeamNaamNormalisatie.NormaliseerVoorVergelijking("T-availsvc JO13-1", Club)));

        return conn;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Naam, object Waarde)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde);
        await cmd.ExecuteNonQueryAsync();
    }
}
