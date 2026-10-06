using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Leren;
using Planner.Shared;
using Planner.Shared.Leren;
using Xunit;

namespace Planner.Endpoints.Tests.Leren;

/// <summary>Alias aanmaken/valideren/verwijderen door een beheerder (#1568 deel C).</summary>
public class TeamAliasEndpointCoreTests
{
    private const string Club = "TESTCLUB";
    private static readonly LerenAanroeper Wie = new("oid-1", "Testbeheerder");

    private static string Body(string tekst = "j10-04", int teamId = 4, bool herkoppel = false)
        => $"{{\"ruweTekst\":\"{tekst}\",\"teamId\":{teamId},\"herkoppel\":{herkoppel.ToString().ToLowerInvariant()},\"herkomstVerwerkingId\":123,\"reden\":\"uit trace\"}}";

    private static Task<IActionResult> Aanmaken(string? body, FakeAliasStore alias, FakeWachtrij wachtrij)
        => TeamAliasEndpointCore.AanmakenAsync(Club, body, Wie, alias, wachtrij, NullLogger.Instance);

    [Fact]
    public async Task Aanmaken_GeeftEen201_ZetGenormaliseerdeSleutelEnAanroeperDoorEnHandeltDeWachtrijAf()
    {
        var alias = new FakeAliasStore();
        var wachtrij = new FakeWachtrij();

        var result = await Aanmaken(Body(), alias, wachtrij);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        var opdracht = alias.Opdracht!;
        opdracht.ClubCode.Should().Be(Club);
        opdracht.RuweTekst.Should().Be("j10-04");
        opdracht.Genormaliseerd.Should().Be(TeamNaamNormalisatie.NormaliseerVoorVergelijking("j10-04", Club), "de sleutel komt uit de ene normalisatiefunctie");
        opdracht.TeamId.Should().Be(4);
        opdracht.Herkoppel.Should().BeFalse();
        opdracht.Wie.Should().Be(Wie);
        opdracht.HerkomstVerwerkingId.Should().Be(123);
        opdracht.Reden.Should().Be("uit trace");
        wachtrij.Afgehandeld.Should().ContainSingle().Which.Should().Be((Club, opdracht.Genormaliseerd));
    }

    [Fact]
    public async Task Aanmaken_DeAanmakerKomtNooitUitDeBody()
    {
        var alias = new FakeAliasStore();
        var body = "{\"ruweTekst\":\"j10-04\",\"teamId\":4,\"aangemaaktDoor\":\"iemand-anders\",\"wie\":{\"objectId\":\"x\"}}";

        await Aanmaken(body, alias, new FakeWachtrij());

        alias.Opdracht!.Wie.Should().Be(Wie);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("geen json")]
    [InlineData("{\"ruweTekst\":\"\",\"teamId\":4}")]
    [InlineData("{\"ruweTekst\":\"j10-04\",\"teamId\":0}")]
    public async Task Aanmaken_ZonderTekstOfTeam_Geeft400(string? body)
    {
        var alias = new FakeAliasStore();

        var result = await Aanmaken(body, alias, new FakeWachtrij());

        result.Should().BeOfType<BadRequestObjectResult>();
        alias.Opdracht.Should().BeNull();
    }

    [Fact]
    public async Task Aanmaken_TeLangeTekst_Geeft400()
        => (await Aanmaken(Body(new string('x', TeamAliasEndpointCore.MaxRuweTekstLengte + 1)), new FakeAliasStore(), new FakeWachtrij()))
            .Should().BeOfType<BadRequestObjectResult>();

    [Fact]
    public async Task Aanmaken_TekstZonderHerkenbareTeamaanduiding_Geeft400()
        => (await Aanmaken(Body("(2025-2026)"), new FakeAliasStore(), new FakeWachtrij())).Should().BeOfType<BadRequestObjectResult>();

    [Fact]
    public async Task Aanmaken_OnbekendTeam_Geeft404_EnLaatDeWachtrijStaan()
    {
        var alias = new FakeAliasStore { Uitkomst = new(AliasAanmaakStatus.TeamOnbekend) };
        var wachtrij = new FakeWachtrij();

        var result = await Aanmaken(Body(), alias, wachtrij);

        result.Should().BeOfType<NotFoundObjectResult>();
        wachtrij.Afgehandeld.Should().BeEmpty();
    }

    [Fact]
    public async Task Aanmaken_BestaandeAliasVoorAnderTeam_Geeft409MetHetBestaandeTeam_ZonderStilleHerkoppeling()
    {
        var alias = new FakeAliasStore { Uitkomst = new(AliasAanmaakStatus.Conflict, 9, null, 7, "TESTCLUB O10-1", "validated") };
        var wachtrij = new FakeWachtrij();

        var result = await Aanmaken(Body(), alias, wachtrij);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value!.ToString().Should().Contain("TESTCLUB O10-1").And.Contain("herkoppelen");
        alias.Opdracht!.Herkoppel.Should().BeFalse("herkoppelen gebeurt alleen als de beheerder dat expliciet aangeeft");
        wachtrij.Afgehandeld.Should().BeEmpty();
    }

    [Fact]
    public async Task Aanmaken_MetExpliciteHerkoppeling_GeeftDeVlagDoor_EnMeldtHerkoppeld()
    {
        var alias = new FakeAliasStore { Uitkomst = new(AliasAanmaakStatus.Herkoppeld, 9, "TESTCLUB O10-4") };

        var result = await Aanmaken(Body(herkoppel: true), alias, new FakeWachtrij());

        alias.Opdracht!.Herkoppel.Should().BeTrue();
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value!.ToString().Should().Contain("herkoppeld");
    }

    [Fact]
    public async Task Aanmaken_AliasBestondAlVoorHetzelfdeTeam_Geeft200BestaatAl()
    {
        var alias = new FakeAliasStore { Uitkomst = new(AliasAanmaakStatus.BestaatAl, 9, "TESTCLUB O10-4") };

        var result = await Aanmaken(Body(), alias, new FakeWachtrij());

        result.Should().BeOfType<OkObjectResult>().Which.Value!.ToString().Should().Contain("bestaat-al");
    }

    [Fact]
    public async Task Aanmaken_EenStoringBijHetAfhandelenVanDeWachtrij_LaatDeAliasStaan()
    {
        var wachtrij = new FakeWachtrij { FaalBijAfhandelen = true };

        var result = await Aanmaken(Body(), new FakeAliasStore(), wachtrij);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    // ── Valideren / verwijderen ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Valideer_GeeftStatusEnAanroeperDoorAanDeOpslag()
    {
        (int, string, LerenAanroeper)? gezien = null;

        var result = await TeamAliasEndpointCore.ValideerAsync(3, "{\"status\":\"validated\"}", Wie,
            (id, status, wie) => { gezien = (id, status, wie); return Task.FromResult(1); });

        result.Should().BeOfType<OkObjectResult>();
        gezien.Should().Be((3, "validated", Wie));
    }

    [Theory]
    [InlineData("{\"status\":\"pending\"}")]
    [InlineData("{}")]
    [InlineData("")]
    public async Task Valideer_OngeldigeStatus_Geeft400(string body)
        => (await TeamAliasEndpointCore.ValideerAsync(3, body, Wie, (_, _, _) => Task.FromResult(1))).Should().BeOfType<BadRequestObjectResult>();

    [Fact]
    public async Task Valideer_OnbekendId_Geeft404()
        => (await TeamAliasEndpointCore.ValideerAsync(3, "{\"status\":\"rejected\"}", Wie, (_, _, _) => Task.FromResult(0)))
            .Should().BeOfType<NotFoundObjectResult>();

    [Fact]
    public async Task Verwijder_BestaandeAlias_Geeft200_OnbekendeAlias_Geeft404()
    {
        (await TeamAliasEndpointCore.VerwijderAsync(3, NullLogger.Instance, _ => Task.FromResult(1))).Should().BeOfType<OkObjectResult>();
        (await TeamAliasEndpointCore.VerwijderAsync(3, NullLogger.Instance, _ => Task.FromResult(0))).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Aanmaken_Dubbelzinnig_Geeft409MetCodeEnKandidaten_ZonderBevestiging()
    {
        var alias = new FakeAliasStore { Uitkomst = new(AliasAanmaakStatus.Dubbelzinnig, Kandidaten: ["TESTCLUB JO13-1", "TESTCLUB MO13-1"]) };

        var result = await Aanmaken(Body("13-1"), alias, new FakeWachtrij());

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(conflict.Value);
        json.Should().Contain("\"code\":\"dubbelzinnig\"").And.Contain("JO13-1").And.Contain("MO13-1").And.Contain("álle mails");
        alias.Opdracht!.BevestigDubbelzinnig.Should().BeFalse();
    }

    [Fact]
    public async Task Aanmaken_MetBevestigDubbelzinnig_GaatMeeNaarDeStore()
    {
        var alias = new FakeAliasStore();
        await Aanmaken("{\"ruweTekst\":\"13-1\",\"teamId\":4,\"bevestigDubbelzinnig\":true}", alias, new FakeWachtrij());
        alias.Opdracht!.BevestigDubbelzinnig.Should().BeTrue();
    }

    [Fact]
    public async Task Aanmaken_Conflict_NoemtHetAantalGeraaktteRijen()
    {
        var alias = new FakeAliasStore { Uitkomst = new(AliasAanmaakStatus.Conflict, 9, null, 2, "TESTCLUB O10-1", "validated", AantalRijenGeraakt: 2) };

        var result = await Aanmaken(Body(), alias, new FakeWachtrij());

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(result.Should().BeOfType<ConflictObjectResult>().Subject.Value);
        json.Should().Contain("\"code\":\"conflict\"").And.Contain("2 alias-rij(en)").And.Contain("\"aantalRijen\":2");
    }

    [Fact]
    public async Task Aanmaken_GelijktijdigAangemaakt_Geeft409BestaatAl_GeenAfhandelingVanDeWachtrij()
    {
        var alias = new FakeAliasStore { Uitkomst = new(AliasAanmaakStatus.GelijktijdigAangemaakt) };
        var wachtrij = new FakeWachtrij();

        var result = await Aanmaken(Body(), alias, wachtrij);

        Newtonsoft.Json.JsonConvert.SerializeObject(result.Should().BeOfType<ConflictObjectResult>().Subject.Value)
            .Should().Contain("\"code\":\"bestaat-al\"");
        wachtrij.Afgehandeld.Should().BeEmpty();
    }

    /// <summary>AVG (M2): het log bevat het alias-id, nooit een identificator van de beheerder.</summary>
    [Fact]
    public async Task Verwijder_LogtGeenIdentificatorVanDeBeheerder()
    {
        var log = new OpnemendLog();
        await TeamAliasEndpointCore.VerwijderAsync(3, log, _ => Task.FromResult(1));

        log.Regels.Should().ContainSingle().Which.Should().Contain("3").And.NotContain(Wie.DoorId).And.NotContain("Testbeheerder");
    }

    private sealed class OpnemendLog : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Regels { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Regels.Add(formatter(state, exception));
    }
}
