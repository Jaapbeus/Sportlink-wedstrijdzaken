using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Teambegeleiding;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Endpoints.Tests.Teambegeleiding;

/// <summary>
/// #1671: rolgrens op het ontvangersveld en gebruikslimiet van het doorstuur-endpoint, gedeeld door
/// beide tiers. Een niet-beheerder mag alleen adressen opgeven die bij het gekozen team horen; alleen
/// een daadwerkelijke verzending van een niet-beheerder telt mee in de limiet.
/// </summary>
public class TeambegeleidingDoorsturenEndpointCoreTests
{
    private static readonly string?[] TeamAdressen = ["trainer@voorbeeld.nl", "coach@voorbeeld.nl", null, "planner@allstars-fc.test"];

    private static GebruikerLimiet NieuweLimiet(int max = 3) => new(max, TimeSpan.FromMinutes(10));

    private static Task<(List<string>? Ontvangers, IActionResult? Fout)> Beoordeel(string regel, bool isAdmin, Action? bijTeamLookup = null) =>
        TeambegeleidingDoorsturenEndpointCore.BeoordeelOntvangersveldAsync(
            regel, isAdmin,
            () => { bijTeamLookup?.Invoke(); return Task.FromResult<IEnumerable<string?>>(TeamAdressen); },
            () => Task.FromResult(new HashSet<string>(["uitgesloten@voorbeeld.nl"], StringComparer.OrdinalIgnoreCase)));

    private static int? Status(IActionResult? resultaat) => resultaat switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        _ => null
    };

    // ── Rolgrens ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task User_AdressenUitHetTeam_InDeVormVanDeBeheerpagina_Passeert()
    {
        var (ontvangers, fout) = await Beoordeel("\"Jan de Vries\" <trainer@voorbeeld.nl>; \"Coach\" <coach@voorbeeld.nl>", isAdmin: false);

        fout.Should().BeNull();
        ontvangers.Should().BeEquivalentTo(["trainer@voorbeeld.nl", "coach@voorbeeld.nl"]);
    }

    [Fact]
    public async Task User_AdresBuitenHetTeam_Geeft403()
    {
        var (ontvangers, fout) = await Beoordeel("buiten-team@voorbeeld.nl", isAdmin: false);

        ontvangers.Should().BeNull();
        Status(fout).Should().Be(403);
    }

    [Fact]
    public async Task User_MixVanTeamadresEnAdresBuitenHetTeam_Geeft403()
    {
        var (_, fout) = await Beoordeel("trainer@voorbeeld.nl; buiten-team@voorbeeld.nl", isAdmin: false);

        Status(fout).Should().Be(403);
    }

    [Fact]
    public async Task User_HoofdlettersEnSpaties_ZijnGelijkwaardig()
    {
        var (ontvangers, fout) = await Beoordeel("  TRAINER@Voorbeeld.NL ;  Planner@ALLSTARS-fc.test ", isAdmin: false);

        fout.Should().BeNull();
        ontvangers.Should().HaveCount(2);
    }

    [Fact]
    public async Task User_AdresBuitenHetTeam_RaadpleegtDeUitsluitingslijstNiet()
    {
        var (_, fout) = await TeambegeleidingDoorsturenEndpointCore.BeoordeelOntvangersveldAsync(
            "uitgesloten@voorbeeld.nl", false,
            () => Task.FromResult<IEnumerable<string?>>(TeamAdressen),
            () => throw new InvalidOperationException("uitsluitingslijst hoort niet geraadpleegd te worden"));

        Status(fout).Should().Be(403);
    }

    [Fact]
    public async Task Admin_VrijAdres_Passeert_ZonderTeamadressenOpTeHalen()
    {
        var lookups = 0;

        var (ontvangers, fout) = await Beoordeel("buiten-team@voorbeeld.nl", isAdmin: true, () => lookups++);

        fout.Should().BeNull();
        ontvangers.Should().Equal("buiten-team@voorbeeld.nl");
        lookups.Should().Be(0);
    }

    [Fact]
    public async Task OngeldigeRegel_Geeft400()
    {
        var (_, fout) = await Beoordeel("geen-adres", isAdmin: false);

        Status(fout).Should().Be(400);
    }

    [Fact]
    public async Task AdresOpDeUitsluitingslijst_Geeft400()
    {
        var (_, fout) = await Beoordeel("Uitgesloten@voorbeeld.nl", isAdmin: true);

        Status(fout).Should().Be(400);
    }

    [Fact]
    public void HorenBijTeam_ToegestaneWaardeMetMeerdereAdressen_TeltElkAdres()
    {
        TeambegeleidingDoorsturenEndpointCore.HorenBijTeam(["b@voorbeeld.nl"], ["\"Planner\" <a@voorbeeld.nl>; b@voorbeeld.nl"])
            .Should().BeTrue();
        TeambegeleidingDoorsturenEndpointCore.HorenBijTeam(["c@voorbeeld.nl"], ["a@voorbeeld.nl; b@voorbeeld.nl", null, " "])
            .Should().BeFalse();
    }

    // ── Toegestane set en leeg veld ──────────────────────────────────────────────────────

    private static OntvangerBronnen Bronnen(string? voorkeur = "trainer@voorbeeld.nl", Action? bijVoorkeur = null) => new(
        () => Task.FromResult<IEnumerable<string>>(["trainer@voorbeeld.nl", "coach@voorbeeld.nl"]),
        () => { bijVoorkeur?.Invoke(); return Task.FromResult(voorkeur); },
        () => Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

    private static readonly Aanvrager User = new(false, "Jan.de.Vries@allstars-fc.test", "gebruiker-a");

    [Fact]
    public async Task User_CoordinatorEnEigenAdres_HorenBijDeToegestaneSet()
    {
        var (ontvangers, fout) = await TeambegeleidingDoorsturenEndpointCore.BepaalOntvangersAsync(
            "planner@allstars-fc.test; jan.de.vries@allstars-fc.test; coach@voorbeeld.nl", User, "planner@allstars-fc.test", Bronnen(), NullLogger.Instance);

        fout.Should().BeNull();
        ontvangers.Should().HaveCount(3);
    }

    [Fact]
    public async Task User_AdresBuitenBegeleidersCoordinatorEnAanvrager_Geeft403()
    {
        var (_, fout) = await TeambegeleidingDoorsturenEndpointCore.BepaalOntvangersAsync(
            "buiten-team@voorbeeld.nl", User, "planner@allstars-fc.test", Bronnen(), NullLogger.Instance);

        Status(fout).Should().Be(403);
    }

    [Fact]
    public async Task LeegVeld_GebruiktDeVoorkeursbegeleider()
    {
        var lookups = 0;

        var (ontvangers, fout) = await TeambegeleidingDoorsturenEndpointCore.BepaalOntvangersAsync(
            "  ", User, "planner@allstars-fc.test", Bronnen(bijVoorkeur: () => lookups++), NullLogger.Instance);

        fout.Should().BeNull();
        ontvangers.Should().Equal("trainer@voorbeeld.nl");
        lookups.Should().Be(1);
    }

    [Fact]
    public async Task LeegVeld_ZonderBegeleider_ValtTerugOpDeCoordinator()
    {
        var (ontvangers, _) = await TeambegeleidingDoorsturenEndpointCore.BepaalOntvangersAsync(
            null, User, "planner@allstars-fc.test", Bronnen(voorkeur: null), NullLogger.Instance);

        ontvangers.Should().Equal("planner@allstars-fc.test");
    }

    [Fact]
    public async Task LeegVeld_ZonderBegeleiderEnCoordinator_Geeft503()
    {
        var (_, fout) = await TeambegeleidingDoorsturenEndpointCore.BepaalOntvangersAsync(
            null, User, null, Bronnen(voorkeur: null), NullLogger.Instance);

        Status(fout).Should().Be(503);
    }

    // ── Volgorde en limiet ──────────────────────────────────────────────────────────────

    private sealed class Verzender
    {
        public int Aantal { get; private set; }
        public Task Verstuur(List<string> _) { Aantal++; return Task.CompletedTask; }
    }

    private static Task<IActionResult> VoerUit(
        bool isAdmin, GebruikerLimiet limiet, Verzender verzender, IActionResult? ontvangerFout = null, bool emailBeschikbaar = true) =>
        TeambegeleidingDoorsturenEndpointCore.VoerUitAsync(
            new Aanvrager(isAdmin, "gebruiker@allstars-fc.test", "gebruiker-a"),
            () => Task.FromResult<(List<string>?, IActionResult?)>(ontvangerFout is null ? (["trainer@voorbeeld.nl"], null) : (null, ontvangerFout)),
            emailBeschikbaar, verzender.Verstuur, "JO13-1", NullLogger.Instance, limiet);

    [Fact]
    public async Task User_VierdeGeldigeVerzending_Geeft429()
    {
        var limiet = NieuweLimiet();
        var verzender = new Verzender();
        for (var i = 0; i < 3; i++)
            Status(await VoerUit(false, limiet, verzender)).Should().Be(200);

        Status(await VoerUit(false, limiet, verzender)).Should().Be(429);
        verzender.Aantal.Should().Be(3);
    }

    [Fact]
    public async Task User_AfgewezenOfMisluktePogingen_VerbruikenGeenSlot()
    {
        var limiet = NieuweLimiet();
        var verzender = new Verzender();

        for (var i = 0; i < 3; i++)
        {
            Status(await VoerUit(false, limiet, verzender, new ObjectResult(new { error = "x" }) { StatusCode = 403 })).Should().Be(403);
            Status(await VoerUit(false, limiet, verzender, new BadRequestObjectResult(new { error = "x" }))).Should().Be(400);
            Status(await VoerUit(false, limiet, verzender, emailBeschikbaar: false)).Should().Be(503);
        }

        for (var i = 0; i < 3; i++)
            Status(await VoerUit(false, limiet, verzender)).Should().Be(200);
        verzender.Aantal.Should().Be(3);
    }

    [Fact]
    public async Task User_MisluktVerzending_GeeftHetSlotTerugEnGeeftDeFoutDoor()
    {
        var limiet = NieuweLimiet();
        var verzender = new Verzender();

        for (var i = 0; i < 3; i++)
        {
            var poging = () => TeambegeleidingDoorsturenEndpointCore.VoerUitAsync(
                new Aanvrager(false, "gebruiker@allstars-fc.test", "gebruiker-a"),
                () => Task.FromResult<(List<string>?, IActionResult?)>((["trainer@voorbeeld.nl"], null)),
                true, _ => throw new InvalidOperationException("verzending mislukt"), "JO13-1", NullLogger.Instance, limiet);
            await poging.Should().ThrowAsync<InvalidOperationException>();
        }

        Status(await VoerUit(false, limiet, verzender)).Should().Be(200);
        verzender.Aantal.Should().Be(1);
    }

    [Fact]
    public async Task User_GeslaagdeVerzending_HoudtHetSlotBezet()
    {
        var limiet = NieuweLimiet(1);
        var verzender = new Verzender();

        Status(await VoerUit(false, limiet, verzender)).Should().Be(200);

        Status(await VoerUit(false, limiet, verzender)).Should().Be(429);
        verzender.Aantal.Should().Be(1);
    }

    [Fact]
    public async Task Admin_ValtBuitenDeLimiet()
    {
        var limiet = NieuweLimiet(1);
        var verzender = new Verzender();

        for (var i = 0; i < 5; i++)
            Status(await VoerUit(true, limiet, verzender)).Should().Be(200);
        verzender.Aantal.Should().Be(5);
    }

    [Fact]
    public void NeemVerzendslot_BovenDeLimiet_Geeft429()
    {
        var limiet = NieuweLimiet(1);

        TeambegeleidingDoorsturenEndpointCore.NeemVerzendslot(false, "gebruiker-a", limiet).Should().BeNull();
        Status(TeambegeleidingDoorsturenEndpointCore.NeemVerzendslot(false, "gebruiker-a", limiet)).Should().Be(429);
        TeambegeleidingDoorsturenEndpointCore.NeemVerzendslot(true, "gebruiker-a", limiet).Should().BeNull();
    }
}
