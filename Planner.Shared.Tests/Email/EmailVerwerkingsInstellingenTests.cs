using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public sealed class EmailVerwerkingsInstellingenTests
{
    private static readonly EmailVerwerkingsInstellingen Geimporteerd =
        new(true, true, "trainer@voorbeeld.nl", true, false);

    [Fact]
    public void VeiligeStandaard_IsVerwerkingUitReviewAanEnOngeinitialiseerd()
    {
        var instellingen = EmailVerwerkingsInstellingen.VeiligeStandaard;

        instellingen.VerwerkingIngeschakeld.Should().BeFalse();
        instellingen.ReviewModusActief.Should().BeTrue();
        instellingen.ReviewOntvanger.Should().BeNull();
        instellingen.IsGeinitialiseerd.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, null, EmailVerwerkingsInstellingenLeesStatus.Onleesbaar)]
    [InlineData(true, null, EmailVerwerkingsInstellingenLeesStatus.RijOntbreekt)]
    public void LeesVeilig_BijFoutOfOntbrekendeRij_GeeftVeiligeStandaard(
        bool databaseLezingGelukt,
        EmailVerwerkingsInstellingen? opgeslagen,
        EmailVerwerkingsInstellingenLeesStatus verwacht)
    {
        var resultaat = EmailVerwerkingsInstellingenLezer.LeesVeilig(opgeslagen, databaseLezingGelukt);

        resultaat.Status.Should().Be(verwacht);
        resultaat.Instellingen.Should().Be(EmailVerwerkingsInstellingen.VeiligeStandaard);
    }

    [Fact]
    public void LeesVeilig_BijNietGeinitialiseerdeRij_GeeftVeiligeStandaard()
    {
        var resultaat = EmailVerwerkingsInstellingenLezer.LeesVeilig(
            EmailVerwerkingsInstellingen.VeiligeStandaard,
            databaseLezingGelukt: true);

        resultaat.Status.Should().Be(EmailVerwerkingsInstellingenLeesStatus.NietGeinitialiseerd);
        resultaat.Instellingen.Should().Be(EmailVerwerkingsInstellingen.VeiligeStandaard);
    }

    [Fact]
    public void LeesVeilig_BijGeimporteerdeRij_LeestEnNormaliseertOntvanger()
    {
        var opgeslagen = Geimporteerd with { ReviewOntvanger = " trainer@voorbeeld.nl " };

        var resultaat = EmailVerwerkingsInstellingenLezer.LeesVeilig(opgeslagen, databaseLezingGelukt: true);

        resultaat.Status.Should().Be(EmailVerwerkingsInstellingenLeesStatus.Beschikbaar);
        resultaat.Instellingen.ReviewOntvanger.Should().Be("trainer@voorbeeld.nl");
        resultaat.Instellingen.VerwerkingIngeschakeld.Should().BeTrue();
    }

    [Fact]
    public void LeesVeilig_BijOngeldigeOntvanger_GeeftVeiligeStandaard()
    {
        var resultaat = EmailVerwerkingsInstellingenLezer.LeesVeilig(
            Geimporteerd with { ReviewOntvanger = "geen-adres" },
            databaseLezingGelukt: true);

        resultaat.Status.Should().Be(EmailVerwerkingsInstellingenLeesStatus.OntvangerOngeldig);
        resultaat.Instellingen.Should().Be(EmailVerwerkingsInstellingen.VeiligeStandaard);
    }

    [Fact]
    public void ValideerWijziging_VereistBevestigingOmReviewUitTeZetten()
    {
        var uit = Geimporteerd with { ReviewModusActief = false };

        var huidig = EmailVerwerkingsInstellingenLezer.LeesVeilig(Geimporteerd, true);
        EmailVerwerkingsInstellingenValidatie.ValideerWijziging(huidig, uit, false)
            .Should().ContainSingle().Which
            .Should().Be(EmailVerwerkingsInstellingenValidatieFout.BevestigingReviewUitzettenVereist);

        EmailVerwerkingsInstellingenValidatie.ValideerWijziging(huidig, uit, true)
            .Should().BeEmpty();
    }

    [Fact]
    public void ValideerWijziging_BijOntbrekendeHuidigeRij_BehoudtReviewAlsVeiligeUitgangswaarde()
    {
        var uit = Geimporteerd with { ReviewModusActief = false };

        var huidig = EmailVerwerkingsInstellingenLezer.LeesVeilig(null, true);
        EmailVerwerkingsInstellingenValidatie.ValideerWijziging(huidig, uit, false)
            .Should().Contain(EmailVerwerkingsInstellingenValidatieFout.BevestigingReviewUitzettenVereist);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValideerWijziging_GebruiktVeiligeStandBijOngeinitialiseerdeOfOnleesbareRij(bool lezingGelukt)
    {
        var opgeslagen = EmailVerwerkingsInstellingen.VeiligeStandaard with { ReviewModusActief = false };
        var huidig = EmailVerwerkingsInstellingenLezer.LeesVeilig(opgeslagen, lezingGelukt);

        huidig.Instellingen.ReviewModusActief.Should().BeTrue();
        EmailVerwerkingsInstellingenValidatie.ValideerWijziging(
                huidig, opgeslagen.NaBeheerderWijziging(), reviewUitzettenBevestigd: false)
            .Should().Contain(EmailVerwerkingsInstellingenValidatieFout.BevestigingReviewUitzettenVereist);
    }

    [Fact]
    public void ValideerWijziging_VraagtGeenTweedeBevestigingAlsReviewAlEffectiefUitStaat()
    {
        var opgeslagen = Geimporteerd with { ReviewModusActief = false };
        var huidig = EmailVerwerkingsInstellingenLezer.LeesVeilig(opgeslagen, true);

        EmailVerwerkingsInstellingenValidatie.ValideerWijziging(
                huidig, opgeslagen.NaBeheerderWijziging(), reviewUitzettenBevestigd: false)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("  ", null, true)]
    [InlineData("trainer@voorbeeld.nl", "trainer@voorbeeld.nl", true)]
    [InlineData("display <trainer@voorbeeld.nl>", null, false)]
    [InlineData("trainer@voorbeeld.nl; ander@voorbeeld.nl", null, false)]
    [InlineData("trainer@voorbeeld.nl, ander@voorbeeld.nl", null, false)]
    [InlineData("geen-adres", null, false)]
    public void TryNormaliseerOntvanger_ValideertEenOptioneelKaalEmailadres(
        string? invoer,
        string? verwacht,
        bool geldig)
    {
        var resultaat = EmailVerwerkingsInstellingenValidatie.TryNormaliseerOntvanger(invoer, out var ontvanger);

        resultaat.Should().Be(geldig);
        ontvanger.Should().Be(geldig ? verwacht : null);
    }

    [Fact]
    public void RedigeerAuditwaarden_BewaartAlleenIngesteldStatusEnGeenAdres()
    {
        var resultaat = EmailOntvangerAuditRedactie.Redigeer(null, "trainer@voorbeeld.nl");

        resultaat.OudeWaarde.Should().Be(EmailOntvangerAuditRedactie.NietIngesteld);
        resultaat.NieuweWaarde.Should().Be(EmailOntvangerAuditRedactie.Ingesteld);
        (resultaat.OudeWaarde + resultaat.NieuweWaarde).Should().NotContain("trainer@voorbeeld.nl");
    }

    [Fact]
    public void DiagnostischeTekst_BevatGeenOntvangeradres()
    {
        var tekst = Geimporteerd.ToString();

        tekst.Should().Contain(EmailOntvangerAuditRedactie.Ingesteld);
        tekst.Should().NotContain("trainer@voorbeeld.nl");
    }

    [Theory]
    [InlineData(null, false, EmailVerwerkingsInstellingenImportBeslissing.BronOntbreekt)]
    [InlineData(null, true, EmailVerwerkingsInstellingenImportBeslissing.Importeren)]
    [InlineData(true, true, EmailVerwerkingsInstellingenImportBeslissing.AlGeimporteerd)]
    [InlineData(false, false, EmailVerwerkingsInstellingenImportBeslissing.BeheerderHeeftGewijzigd)]
    public void ImportBeslissing_IsEenmaligEnLaatBeheerderwijzigingVoorgaan(
        bool? alGeimporteerd,
        bool bronBeschikbaar,
        EmailVerwerkingsInstellingenImportBeslissing verwacht)
    {
        var huidig = alGeimporteerd switch
        {
            true => Geimporteerd,
            false => Geimporteerd with { EenmaligeImportVoltooid = false, DoorBeheerderGewijzigd = true },
            _ => null
        };

        EmailVerwerkingsInstellingenImport.Bepaal(huidig, bronBeschikbaar).Should().Be(verwacht);
    }

    [Fact]
    public void ImportBeslissing_SlaatAfwijkendeWaardenOverOokZonderMarker()
    {
        var huidig = EmailVerwerkingsInstellingen.VeiligeStandaard with { ReviewModusActief = false };

        EmailVerwerkingsInstellingenImport.Bepaal(huidig, legacyBronBeschikbaar: true)
            .Should().Be(EmailVerwerkingsInstellingenImportBeslissing.HuidigeWaardenGewijzigd);
    }

    [Fact]
    public void Beheerwijziging_VereistBevestigingVanVeiligeStandEnNormaliseertAdres()
    {
        var huidig = EmailVerwerkingsInstellingenLezer.LeesVeilig(
            EmailVerwerkingsInstellingen.VeiligeStandaard with { ReviewModusActief = false }, true);
        var velden = new Dictionary<string, string?>
        {
            ["EmailVerwerkingIngeschakeld"] = "1",
            ["EmailReviewModusActief"] = "0",
            ["EmailReviewOntvanger"] = " trainer@voorbeeld.nl "
        };

        var fout = () => EmailVerwerkingsInstellingenWijziging.Bouw(huidig, velden, false);
        fout.Should().Throw<EmailVerwerkingsInstellingenWijzigingException>()
            .Which.Fout.Should().Be(EmailVerwerkingsInstellingenValidatieFout.BevestigingReviewUitzettenVereist);

        var nieuw = EmailVerwerkingsInstellingenWijziging.Bouw(huidig, velden, true);
        EmailVerwerkingsInstellingenWijziging.VulVeldenAan(velden, nieuw);
        nieuw.DoorBeheerderGewijzigd.Should().BeTrue();
        nieuw.ReviewOntvanger.Should().Be("trainer@voorbeeld.nl");
        velden["EmailReviewOntvanger"].Should().Be("trainer@voorbeeld.nl");
    }

    [Fact]
    public void Beheerwijziging_OngeldigAdresGeeftGeenAdresInFoutOfAudit()
    {
        var huidig = EmailVerwerkingsInstellingenLezer.LeesVeilig(Geimporteerd, true);
        var velden = new Dictionary<string, string?> { ["EmailReviewOntvanger"] = "ongeldig@" };

        var fout = () => EmailVerwerkingsInstellingenWijziging.Bouw(huidig, velden, false);
        var uitzondering = fout.Should().Throw<EmailVerwerkingsInstellingenWijzigingException>().Which;
        uitzondering.VeiligeMelding.Should().NotContain("ongeldig@");
        EmailVerwerkingsInstellingenWijziging.AuditWaarden(
            "EmailReviewOntvanger", Geimporteerd.ReviewOntvanger, "ongeldig@")
            .ToString().Should().NotContain("@");
    }

    [Fact]
    public void Beheerwijziging_WeigertOngeldigeSchakelaar()
    {
        var huidig = EmailVerwerkingsInstellingenLezer.LeesVeilig(Geimporteerd, true);
        var velden = new Dictionary<string, string?> { ["EmailVerwerkingIngeschakeld"] = "aan misschien" };

        var fout = () => EmailVerwerkingsInstellingenWijziging.Bouw(huidig, velden, false);
        fout.Should().Throw<EmailVerwerkingsInstellingenWijzigingException>()
            .Which.Fout.Should().Be(EmailVerwerkingsInstellingenValidatieFout.SchakelaarOngeldig);
    }

}
