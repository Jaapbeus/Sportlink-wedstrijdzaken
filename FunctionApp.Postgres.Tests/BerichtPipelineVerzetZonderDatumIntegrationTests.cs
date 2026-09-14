using Database.Postgres;
using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Processing;
using FunctionApp.Postgres.TeamResolution;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Npgsql;
using Planner.Shared;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// End-to-end pipeline-scenario voor het "verzet zonder datum"-pad (#561/#1141) —
/// <see cref="BerichtPipeline.VerwerkMetPlannerAsync"/> +
/// <see cref="BerichtPipeline.BouwTemplateAntwoord"/> samen, zoals ze ook door
/// <c>EmailTestFunction</c>/<c>EmailProcessorFunction</c> worden aangeroepen. Er bestaat geen
/// equivalente test op de SQL Server-tier om te porten (het #561-pad heeft daar nooit een
/// geautomatiseerde test gekregen) — deze klasse is daarom nieuw geschreven, gemodelleerd naar het
/// bestaande zoekvolgorde-/opstellingspatroon van <see cref="PlannerMatchOpponentLookupIntegrationTests"/>.
///
/// <para>
/// <b>Team-resolutie via een vaste fake, geen echte kandidatenlijst</b>: <see cref="FakeTeamResolver"/>
/// lost elke aanvraag onmiddellijk op naar zichzelf. Dat isoleert deze test van
/// <c>TeamResolver</c>/<c>TeamCandidateRepository</c>'s eigen matching-logica (al gedekt door
/// <c>TeamCanonicalisatieIntegrationTests</c> e.a.) — hier gaat het om de KNVB-bijlage-orkestratie
/// ná teamherkenning, niet om teamherkenning zelf.
/// </para>
///
/// <para>
/// <b>Seizoen "1901/9998" domineert bewust</b> — zelfde truc als
/// <see cref="PostgresSeasonHelperKnvbSeizoenIntegrationTests"/>: een <c>datefrom</c> ver in het
/// verleden met een <c>dateuntil</c> ver in de toekomst wint altijd van de echte (migratie-008-
/// geseede) seizoensrijen bij <c>ORDER BY datefrom ASC LIMIT 1</c>, dus deze test hoeft
/// <c>public.season</c> niet leeg te maken.
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class BerichtPipelineVerzetZonderDatumIntegrationTests : IAsyncLifetime
{
    private const string Club = "verzetzonderdatum";
    private const string Accommodatie = "Sportpark VZD Test";
    private const string Team = "JO13-1";
    private const string Regio = "West";
    private const string SeizoenNaam = "1901-9998";
    private const int DeadlineDagen = 8;

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    public async Task InitializeAsync()
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(ConnectionString, KnownEntities.Teams, KnownEntities.Matches);
        PostgresAppSettings.SetForTests("clubCode", Club);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var sql in new[]
        {
            "DELETE FROM his.matches WHERE clubcode = @club",
            "DELETE FROM his.teams WHERE clubcode = @club",
            "DELETE FROM public.teams WHERE clubcode = @club",
            "DELETE FROM public.appsettings WHERE clubcode = @club",
            "DELETE FROM public.speeltijden WHERE clubcode = @club",
            "DELETE FROM public.knvbkalenderdag WHERE seizoen = @seizoen",
            "DELETE FROM public.season WHERE name = @seizoennaam",
        })
            await ExecAsync(conn, sql, ("club", Club), ("seizoen", $"{1901}/{9998}"), ("seizoennaam", SeizoenNaam));

        await ExecAsync(conn,
            "INSERT INTO public.appsettings (clubcode, syncenabled, accommodatie) VALUES (@club, true, @acc)",
            ("club", Club), ("acc", Accommodatie));

        // FindMatchAsync's LEFT JOIN public.speeltijden vereist een rij voor de leeftijdscategorie
        // van het team ('JO13'), anders gooit MapZoekWedstrijdResponse "Speelduur niet
        // geconfigureerd" — zelfde precedent als PlannerMatchOpponentLookupIntegrationTests.
        await ExecAsync(conn,
            "INSERT INTO public.speeltijden (leeftijd, veldafmeting, wedstrijdtotaal, clubcode) VALUES ('JO13', 1.00, 60, @club) ON CONFLICT DO NOTHING",
            ("club", Club));

        await ExecAsync(conn,
            "INSERT INTO public.season (name, datefrom, dateuntil) VALUES (@naam, @van, @tot)",
            ("naam", SeizoenNaam), ("van", new DateTime(1901, 1, 1)), ("tot", new DateTime(9998, 1, 1)));

        var genormaliseerd = TeamNaamNormalisatie.NormaliseerVoorVergelijking(Team, Club);
        await ExecAsync(conn,
            "INSERT INTO public.teams (clubcode, teamnaam, teamnaamgenormaliseerd) VALUES (@club, @team, @genorm)",
            ("club", Club), ("team", Team), ("genorm", genormaliseerd));
    }

    public async Task DisposeAsync()
    {
        PostgresAppSettings.ResetForTests();

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "DELETE FROM his.matches WHERE clubcode = @club",
            "DELETE FROM his.teams WHERE clubcode = @club",
            "DELETE FROM public.teams WHERE clubcode = @club",
            "DELETE FROM public.appsettings WHERE clubcode = @club",
            "DELETE FROM public.speeltijden WHERE clubcode = @club",
            "DELETE FROM public.knvbkalenderdag WHERE seizoen = @seizoen",
            "DELETE FROM public.season WHERE name = @seizoennaam",
        })
            await ExecAsync(conn, sql, ("club", Club), ("seizoen", $"{1901}/{9998}"), ("seizoennaam", SeizoenNaam));
    }

    [PostgresFact]
    public async Task TegenstanderZonderGewensteDatum_GeeftVrijeZaterdagenEnMarkeertBijlage()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        var wedstrijdDatum = vandaag.AddDays(60);
        await ZetMatchAsync(wedstrijdDatum, "10:00");

        // Een geldige "vrije zaterdag"-kandidaat binnen het venster [vandaag+deadline, +56 dagen].
        var van = vandaag.AddDays(DeadlineDagen);
        var vrijeZaterdag = EersteZaterdagVanafOfNa(van);
        await ZetKnvbDagAsync(vrijeZaterdag, "Competitie");

        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.HerplanVerzoek,
            TeamNaam = Team,
            Datum = wedstrijdDatum.ToString("yyyy-MM-dd"),
            NamensWie = NamensWie.Tegenstander,
            GewensteDatum = null,
        };
        var bericht = new InkomendBericht
        {
            MessageId = "msg-1", Afzender = "tegenstander@allstars-fc.test", AfzenderNaam = "Frenkie",
            Onderwerp = "Verzoek tot verzetten", OntvangstDatum = DateTime.UtcNow,
        };
        var clubSettings = new ClubAppSettingsSnapshot(
            PlannerAfzenderNaam: "VZD Testclub", CoordinatorNaam: null, CoordinatorFunctie: null, EmailVoetnoot: null,
            HerplanDeadlineDagen: DeadlineDagen, KnvbPdfBijlageIngeschakeld: true, KnvbStandaardRegio: Regio);

        var responseJson = await BerichtPipeline.VerwerkMetPlannerAsync(
            classificatie, bericht, NullLogger.Instance, new FakeTeamResolver(), Club, clubSettings);

        var response = JObject.Parse(responseJson);
        response["verzetZonderDatum"]?.ToObject<bool>().Should().BeTrue(
            "een tegenstander zonder concrete gewenste datum moet het #561-pad triggeren, niet het standaard herplanpad");
        response["regio"]?.ToString().Should().Be(Regio);
        response["seizoen"]?.ToString().Should().Be("1901/9998");
        var vrijeZaterdagenJson = response["vrijeZaterdagen"]?.ToObject<List<string>>() ?? new();
        vrijeZaterdagenJson.Should().Contain(vrijeZaterdag.ToString("yyyy-MM-dd"));

        classificatie.VoegKnvbPdfBijlageToe.Should().BeTrue(
            "de pipeline moet de classificatie markeren zodat EmailReplyPolicyService de KNVB-PDF als bijlage toevoegt");
        classificatie.KnvbBijlageRegio.Should().Be(Regio);

        var (_, body) = await BerichtPipeline.BouwTemplateAntwoord(
            classificatie, responseJson, bericht, NullLogger.Instance, clubSettings, Club);

        body.Should().Contain("KNVB-speeldagenkalender",
            "het antwoord moet vermelden dat de kalender als bijlage is toegevoegd");
        var nl = new System.Globalization.CultureInfo("nl-NL");
        body.Should().Contain(vrijeZaterdag.ToString("dddd d MMMM yyyy", nl),
            "de voorgestelde vrije zaterdag moet in het antwoord aan de afzender staan");
    }

    [PostgresFact]
    public async Task GeenKnvbStandaardRegio_ValtTerugOpStandaardHerplanpad()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        var wedstrijdDatum = vandaag.AddDays(60);
        await ZetMatchAsync(wedstrijdDatum, "10:00");

        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.HerplanVerzoek,
            TeamNaam = Team,
            Datum = wedstrijdDatum.ToString("yyyy-MM-dd"),
            NamensWie = NamensWie.Tegenstander,
            GewensteDatum = null,
        };
        var bericht = new InkomendBericht
        {
            MessageId = "msg-2", Afzender = "tegenstander@allstars-fc.test", AfzenderNaam = "Frenkie",
            Onderwerp = "Verzoek tot verzetten", OntvangstDatum = DateTime.UtcNow,
        };
        // Bewust GEEN KnvbStandaardRegio — noch via clubSettings, noch via de procesbrede cache
        // (ResetForTests in InitializeAsync/DisposeAsync houdt PostgresAppSettings leeg).
        var clubSettings = new ClubAppSettingsSnapshot(
            PlannerAfzenderNaam: "VZD Testclub", CoordinatorNaam: null, CoordinatorFunctie: null, EmailVoetnoot: null,
            HerplanDeadlineDagen: DeadlineDagen, KnvbPdfBijlageIngeschakeld: true, KnvbStandaardRegio: null);

        var responseJson = await BerichtPipeline.VerwerkMetPlannerAsync(
            classificatie, bericht, NullLogger.Instance, new FakeTeamResolver(), Club, clubSettings);

        var response = JObject.Parse(responseJson);
        response["verzetZonderDatum"].Should().BeNull(
            "zonder knvbStandaardRegio moet het bestaande fallbackgedrag gelden — exact zoals op de SQL Server-tier");
        classificatie.VoegKnvbPdfBijlageToe.Should().BeFalse();
    }

    // ── opstelling ─────────────────────────────────────────────────────────

    private static DateOnly EersteZaterdagVanafOfNa(DateOnly datum)
    {
        while (datum.DayOfWeek != DayOfWeek.Saturday) datum = datum.AddDays(1);
        return datum;
    }

    private async Task ZetMatchAsync(DateOnly datum, string aanvang)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, @"
            INSERT INTO his.matches (wedstrijdcode, kaledatum, aanvangstijd, veld, teamnaam, wedstrijd, accommodatie, status, clubcode, mta_inserted, mta_modified)
            VALUES (9500001, @datum, @aanvang, 'veld 1', @team, @wedstrijd, @acc, 'Te spelen', @club, NOW(), NOW())",
            ("datum", datum.ToDateTime(TimeOnly.MinValue)), ("aanvang", aanvang), ("team", Team),
            ("wedstrijd", $"{Team} - SV Tegenstander"), ("acc", Accommodatie), ("club", Club));

        await ExecAsync(conn, @"
            INSERT INTO his.teams (teamcode, lokaleteamcode, poulecode, teamnaam, leeftijdscategorie, clubcode, mta_inserted, mta_modified)
            SELECT 9500001, 9500001, 9500001, @team, 'JO13', @club, NOW(), NOW()
            WHERE NOT EXISTS (SELECT 1 FROM his.teams WHERE teamnaam = @team AND clubcode = @club)",
            ("team", Team), ("club", Club));
    }

    private async Task ZetKnvbDagAsync(DateOnly datum, string dagtype)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, @"
            INSERT INTO public.knvbkalenderdag
                (seizoen, regio, datum, dagtype, heeftsenioren, heeftjeugd, heeftmeiden, pupillentoernooi)
            VALUES ('1901/9998', @regio, @datum, @dagtype, TRUE, TRUE, FALSE, FALSE)
            ON CONFLICT (seizoen, regio, datum) DO NOTHING",
            ("regio", Regio), ("datum", datum.ToDateTime(TimeOnly.MinValue)), ("dagtype", dagtype));
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Naam, object Waarde)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Lost elke aanvraag onmiddellijk op naar zichzelf — zie klassekop.</summary>
    private sealed class FakeTeamResolver : ITeamResolver
    {
        public Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
            => Task.FromResult(new TeamResolutionResult(
                1, request.RuweTeamTekst, 1.0, Array.Empty<TeamCandidate>(), ResolutionBron.ExacteMatch));
    }
}
