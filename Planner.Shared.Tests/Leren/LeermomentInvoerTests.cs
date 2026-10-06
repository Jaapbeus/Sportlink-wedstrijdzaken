using AwesomeAssertions;
using Planner.Shared.Email;
using Planner.Shared.Leren;
using Xunit;

namespace Planner.Shared.Tests.Leren;

/// <summary>Invoer van een admin-leermoment (#1568 deel C): bekend type, gesaneerde samenvatting, vaste few-shot-vorm.</summary>
public class LeermomentInvoerTests
{
    [Fact]
    public void GeldigeInvoer_GeeftGesaneerdeSamenvattingTerug()
    {
        var (waarde, fout) = LeermomentInvoer.Valideer(null, "HerplanVerzoek", "Verzoek om de wedstrijd van zaterdag  te verzetten, mail jan@voorbeeld.nl");

        fout.Should().BeNull();
        waarde!.OrigineelType.Should().Be(LeermomentInvoer.OnbekendType);
        waarde.JuistType.Should().Be("HerplanVerzoek");
        waarde.Samenvatting.Should().NotContain("@").And.Contain("[e-mail]").And.NotContain("  ");
    }

    [Fact]
    public void Samenvatting_WordtBegrensdOp500_ZonderAanhalingstekens()
    {
        var (waarde, _) = LeermomentInvoer.Valideer("BeschikbaarheidCheck", "HerplanVerzoek", "\"" + new string('x', 900) + "\"");

        waarde!.Samenvatting.Length.Should().BeLessThanOrEqualTo(LeermomentInvoer.MaxSamenvattingLengte);
        waarde.Samenvatting.Should().NotContain("\"");
    }

    [Theory]
    [InlineData("Bestaatniet", "HerplanVerzoek", "tekst")]
    [InlineData(null, "", "tekst")]
    [InlineData("BeschikbaarheidCheck", "HerplanVerzoek", "   ")]
    [InlineData("Onzin", "HerplanVerzoek", "tekst")]
    public void OngeldigeInvoer_GeeftEenFoutmelding(string? origineel, string juist, string samenvatting)
    {
        var (waarde, fout) = LeermomentInvoer.Valideer(origineel, juist, samenvatting);

        waarde.Should().BeNull();
        fout.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void FewShotRegel_ReplyCorrectie_BlijftExactDeBestaandeVorm()
        => LeermomentInvoer.FewShotRegel("BeschikbaarheidCheck", "HerplanVerzoek", "orig", "corr")
            .Should().Be("- Samenvatting: \"orig\" → was geclassificeerd als BeschikbaarheidCheck, maar was eigenlijk HerplanVerzoek. Correctie: \"corr\"");

    [Fact]
    public void FewShotRegel_AdminZonderOrigineelType_ZegtAlleenWatHetIs()
        => LeermomentInvoer.FewShotRegel(LeermomentInvoer.OnbekendType, "HerplanVerzoek", "iemand wil verzetten", null)
            .Should().Be("- Samenvatting: \"iemand wil verzetten\" → is een HerplanVerzoek.");

    [Fact]
    public void FewShotRegel_AdminMetOrigineelType_NoemtDeCorrectie()
        => LeermomentInvoer.FewShotRegel("BeschikbaarheidCheck", "HerplanVerzoek", "iemand wil verzetten", "")
            .Should().Be("- Samenvatting: \"iemand wil verzetten\" → was geclassificeerd als BeschikbaarheidCheck, maar was eigenlijk HerplanVerzoek.");

    [Fact]
    public void FewShotRegel_GeanonimiseerdeReplyRij_BlijftInDeBestaandeVorm()
        => LeermomentInvoer.FewShotRegel("BeschikbaarheidCheck", "HerplanVerzoek", null, null)
            .Should().Contain("Correctie:");
}

public class LerenAanroeperTests
{
    [Fact]
    public void MetPrincipal_GebruiktObjectIdEnNaam()
    {
        var wie = new LerenAanroeper("oid-1", "Testbeheerder");
        wie.DoorId.Should().Be("oid-1");
        wie.DoorNaam.Should().Be("Testbeheerder");
    }

    [Fact]
    public void ZonderPrincipal_IsDeLokaleOntwikkelaar()
        => new LerenAanroeper(null, null).DoorId.Should().Be("lokale-ontwikkelaar");

    [Fact]
    public void MetPrincipalMaarZonderObjectIdEnNaam_IsOnbekend_NooitLokaal()
        => (new LerenAanroeper(null, null) { HeeftPrincipal = true }).DoorId.Should().Be("onbekend");

    [Fact]
    public void ZonderObjectIdMaarMetNaam_IsOnbekend_EnLangeWaardenWordenAfgekapt()
    {
        var wie = new LerenAanroeper(null, new string('n', 300));
        wie.DoorId.Should().Be("onbekend");
        wie.DoorNaam!.Length.Should().Be(LerenAanroeper.MaxNaamLengte);
        new LerenAanroeper(new string('o', 300), null).DoorId.Length.Should().Be(LerenAanroeper.MaxIdLengte);
    }
}
