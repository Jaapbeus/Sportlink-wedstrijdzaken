using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

/// <summary>
/// Tests voor de tier-onafhankelijke kern van de Sportlink Web Extension (#1266).
///
/// Waarom deze tests hier staan en niet per tier: tot #1266 stond deze beslislogica alleen in de
/// Postgres-tier, en werden alleen daar twee van de zes methodes getest. De SQL Server-tier had
/// niets. Nu de logica gedeeld is, dekt één suite beide tiers — precies de structuur die bij #1252
/// ontbrak, waar een fout in gedupliceerde code maandenlang onzichtbaar bleef omdat er nergens
/// één plek was waar een test op kon aangrijpen.
/// </summary>
public class SportlinkEndpointCoreTests
{
    private static Func<string, string?> Instellingen(params (string Sleutel, string? Waarde)[] paren)
        => sleutel => paren.FirstOrDefault(p => p.Sleutel == sleutel).Waarde;

    // ── Dry-run: fail-safe polariteit ───────────────────────────────────────────────────────────
    // Dit is de belangrijkste test van dit bestand. De polariteit is bewust "alles behalve een
    // expliciet geladen 0 is dry-run". Met de omgekeerde vorm (== "1") zou een nog niet geladen
    // instellingencache fail-OPEN zijn: het statuspaneel meldt "dry-run aan" terwijl een
    // bevestigde mutatie écht naar Sportlink gaat.

    [Theory]
    [InlineData(null, true)]      // cache nog niet geladen
    [InlineData("", true)]        // kolom bestaat, waarde leeg
    [InlineData("1", true)]       // expliciet aan
    [InlineData("true", true)]    // onverwachte waarde -> veilige kant
    [InlineData("0", false)]      // de enige waarde die dry-run uitzet
    public void IsDryRunActief_AllesBehalveNul_IsDryRun(string? waarde, bool verwacht)
    {
        var lees = Instellingen((SportlinkEndpointCore.InstellingDryRun, waarde));

        SportlinkEndpointCore.IsDryRunActief(lees).Should().Be(verwacht);
    }

    [Fact]
    public void IsDryRunActief_LezenGooit_BlijftDryRunAan()
    {
        Func<string, string?> lees = _ => throw new InvalidOperationException("database onbereikbaar");

        SportlinkEndpointCore.IsDryRunActief(lees).Should().BeTrue(
            "een leesfout mag nooit tot een echte mutatie naar Sportlink leiden");
    }

    // ── Toggle + EgressGuard ────────────────────────────────────────────────────────────────────

    [Fact]
    public void BepaalTimerStatus_ToggleUit_GeeftExtensieUit()
    {
        var lees = Instellingen((SportlinkEndpointCore.InstellingExtensieIngeschakeld, "0"));

        SportlinkEndpointCore.BepaalTimerStatus(lees, () => true)
            .Should().Be(SportlinkTimerStatus.ExtensieUit);
    }

    [Fact]
    public void BepaalTimerStatus_ToggleOnbekend_GeeftExtensieUit()
    {
        // Ontbrekende instelling telt als uit — een club die de extensie nooit heeft ingericht,
        // mag er niet per ongeluk mee gaan draaien.
        var lees = Instellingen();

        SportlinkEndpointCore.BepaalTimerStatus(lees, () => true)
            .Should().Be(SportlinkTimerStatus.ExtensieUit);
    }

    [Fact]
    public void BepaalTimerStatus_EgressGeblokkeerd_GaatVoorMagDraaien()
    {
        var lees = Instellingen((SportlinkEndpointCore.InstellingExtensieIngeschakeld, "1"));

        SportlinkEndpointCore.BepaalTimerStatus(lees, () => false)
            .Should().Be(SportlinkTimerStatus.EgressGeblokkeerd);
    }

    [Fact]
    public void BepaalTimerStatus_ToggleAanEnEgressToegestaan_MagDraaien()
    {
        var lees = Instellingen((SportlinkEndpointCore.InstellingExtensieIngeschakeld, "1"));

        SportlinkEndpointCore.BepaalTimerStatus(lees, () => true)
            .Should().Be(SportlinkTimerStatus.MagDraaien);
    }

    [Fact]
    public void ControleerToggleEnEgress_ToggleUit_Geeft409()
    {
        var lees = Instellingen((SportlinkEndpointCore.InstellingExtensieIngeschakeld, "0"));

        SportlinkEndpointCore.ControleerToggleEnEgress(lees, () => true)!
            .HttpStatus.Should().Be(409);
    }

    [Fact]
    public void ControleerToggleEnEgress_EgressGeblokkeerd_Geeft503()
    {
        var lees = Instellingen((SportlinkEndpointCore.InstellingExtensieIngeschakeld, "1"));

        SportlinkEndpointCore.ControleerToggleEnEgress(lees, () => false)!
            .HttpStatus.Should().Be(503);
    }

    [Fact]
    public void ControleerToggleEnEgress_AllesGoed_GeeftNull()
    {
        var lees = Instellingen((SportlinkEndpointCore.InstellingExtensieIngeschakeld, "1"));

        SportlinkEndpointCore.ControleerToggleEnEgress(lees, () => true).Should().BeNull();
    }

    // ── Statusvertaling ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SportlinkClubCallStatus.RolNietGekoppeld, 409)]
    [InlineData(SportlinkClubCallStatus.HerkoppelingVereist, 409)]
    [InlineData(SportlinkClubCallStatus.SportlinkFout, 502)]
    [InlineData(SportlinkClubCallStatus.NetwerkFout, 502)]
    public void VertaalStatusNaarFout_GeeftVerwachteHttpStatus(SportlinkClubCallStatus status, int verwacht)
    {
        SportlinkEndpointCore.VertaalStatusNaarFout(status)!.HttpStatus.Should().Be(verwacht);
    }

    [Fact]
    public void VertaalStatusNaarFout_Ok_GeeftNull()
    {
        SportlinkEndpointCore.VertaalStatusNaarFout(SportlinkClubCallStatus.Ok).Should().BeNull();
    }

    [Theory]
    [InlineData(SportlinkClubCallStatus.SportlinkFout)]
    [InlineData(SportlinkClubCallStatus.NetwerkFout)]
    public void VertaalStatusNaarFout_LektGeenSportlinkDetails(SportlinkClubCallStatus status)
    {
        // CISO-regel: de melding die de client ziet bevat nooit de onderliggende foutdetails.
        var fout = SportlinkEndpointCore.VertaalStatusNaarFout(status)!;

        fout.Foutmelding.Should().Be("Sportlink is momenteel niet bereikbaar.");
    }

    // ── Audit-resultaat: volgorde van voorrang ──────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true, true, "DryRunLocked")]    // code-lock gaat vóór alles
    [InlineData(false, true, true, "DryRunLocked")]   // ook als IsSuccess false is
    [InlineData(true, false, true, "DryRun")]         // club-instelling
    [InlineData(true, false, false, "Success")]
    [InlineData(false, false, false, "Failure")]
    public void BepaalAuditResultaat_VolgtDeVastgelegdeVoorrang(
        bool isSuccess, bool isForcedDryRun, bool isDryRun, string verwacht)
    {
        var resultaat = new SportlinkMutationResult(isSuccess, null, isDryRun, isForcedDryRun);

        SportlinkEndpointCore.BepaalAuditResultaat(resultaat).Should().Be(verwacht);
    }

    // ── Mutatie-afronding ───────────────────────────────────────────────────────────────────────

    private sealed record Payload(string Waarde);

    [Fact]
    public void BepaalMutatieAfronding_TransportFout_GeeftFailureEnVertaaldeFout()
    {
        var respons = new SportlinkClubResponse<Payload>(
            SportlinkClubCallStatus.NetwerkFout, null, "timeout na 30s", null);

        var afronding = SportlinkEndpointCore.BepaalMutatieAfronding(respons, _ =>
            new SportlinkMutationResult(true, null));

        afronding.AuditResultaat.Should().Be("Failure");
        afronding.Fout!.HttpStatus.Should().Be(502);
        afronding.Data.Should().BeNull();
        afronding.AuditSamenvatting.Should().Be("timeout na 30s");
    }

    [Fact]
    public void BepaalMutatieAfronding_OkMaarGeenData_Geeft502()
    {
        var respons = new SportlinkClubResponse<Payload>(SportlinkClubCallStatus.Ok, null, null, 200);

        var afronding = SportlinkEndpointCore.BepaalMutatieAfronding(respons, _ =>
            new SportlinkMutationResult(true, null));

        afronding.AuditResultaat.Should().Be("Failure");
        afronding.Fout!.HttpStatus.Should().Be(502);
    }

    [Fact]
    public void BepaalMutatieAfronding_InhoudelijkeAfwijzing_IsGeenHttpFout()
    {
        // Een afwijzing door Sportlink zelf (violations) is een geldig antwoord, geen transportfout:
        // de client krijgt de data en beslist zelf, consistent met AdminApiClient.
        var respons = new SportlinkClubResponse<Payload>(
            SportlinkClubCallStatus.Ok, new Payload("x"), null, 200);

        var afronding = SportlinkEndpointCore.BepaalMutatieAfronding(respons, _ =>
            new SportlinkMutationResult(false, new[] { "Veld bezet", "Buiten speeltijd" }));

        afronding.Fout.Should().BeNull();
        afronding.Data.Should().NotBeNull();
        afronding.AuditResultaat.Should().Be("Failure");
        afronding.AuditSamenvatting.Should().Be("Veld bezet, Buiten speeltijd");
    }

    [Fact]
    public void BepaalMutatieAfronding_DryRun_GeeftDataZonderFout()
    {
        var respons = new SportlinkClubResponse<Payload>(
            SportlinkClubCallStatus.Ok, new Payload("x"), null, 200);

        var afronding = SportlinkEndpointCore.BepaalMutatieAfronding(respons, _ =>
            new SportlinkMutationResult(true, null, IsDryRun: true));

        afronding.Fout.Should().BeNull();
        afronding.AuditResultaat.Should().Be("DryRun");
        afronding.AuditSamenvatting.Should().BeNull();
    }
}
