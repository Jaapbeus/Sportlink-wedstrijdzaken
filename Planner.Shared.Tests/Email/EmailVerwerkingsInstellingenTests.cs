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

        EmailVerwerkingsInstellingenValidatie.ValideerWijziging(Geimporteerd, uit, false)
            .Should().ContainSingle().Which
            .Should().Be(EmailVerwerkingsInstellingenValidatieFout.BevestigingReviewUitzettenVereist);

        EmailVerwerkingsInstellingenValidatie.ValideerWijziging(Geimporteerd, uit, true)
            .Should().BeEmpty();
    }

    [Fact]
    public void ValideerWijziging_BijOntbrekendeHuidigeRij_BehoudtReviewAlsVeiligeUitgangswaarde()
    {
        var uit = Geimporteerd with { ReviewModusActief = false };

        EmailVerwerkingsInstellingenValidatie.ValideerWijziging(null, uit, false)
            .Should().Contain(EmailVerwerkingsInstellingenValidatieFout.BevestigingReviewUitzettenVereist);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("  ", null, true)]
    [InlineData("trainer@voorbeeld.nl", "trainer@voorbeeld.nl", true)]
    [InlineData("display <trainer@voorbeeld.nl>", null, false)]
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

}
