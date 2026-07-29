using FluentAssertions;
using SportlinkFunction.Utilities;
using Xunit;

namespace FunctionApp.Tests.Utilities;

/// <summary>
/// Regressietests voor #765: het "Email Aan"-veld op /teambegeleiding bepaalt nu daadwerkelijk de
/// ontvangers van de doorstuur-mail, dus moet de parser elk aangeboden formaat correct herkennen én
/// nooit stilzwijgend een ongeldig adres overslaan.
/// </summary>
public class OntvangerParserTests
{
    [Fact]
    public void Parse_MetNaamEnAdres_HaaltAlleenAdresEruit()
    {
        var result = OntvangerParser.Parse("\"Jan de Vries\" <trainer@voorbeeld.nl>");

        result.IsValid.Should().BeTrue();
        result.Emailadressen.Should().Equal("trainer@voorbeeld.nl");
    }

    [Fact]
    public void Parse_KaalAdres_WordtGeaccepteerd()
    {
        var result = OntvangerParser.Parse("trainer@voorbeeld.nl");

        result.IsValid.Should().BeTrue();
        result.Emailadressen.Should().Equal("trainer@voorbeeld.nl");
    }

    [Fact]
    public void Parse_GescheidenDoorPuntkomma_GeeftBeideAdressen()
    {
        var result = OntvangerParser.Parse("a@voorbeeld.nl; b@voorbeeld.nl");

        result.IsValid.Should().BeTrue();
        result.Emailadressen.Should().Equal("a@voorbeeld.nl", "b@voorbeeld.nl");
    }

    [Fact]
    public void Parse_GescheidenDoorKomma_GeeftBeideAdressen()
    {
        var result = OntvangerParser.Parse("a@voorbeeld.nl, b@voorbeeld.nl");

        result.IsValid.Should().BeTrue();
        result.Emailadressen.Should().Equal("a@voorbeeld.nl", "b@voorbeeld.nl");
    }

    [Fact]
    public void Parse_DubbeleSpaties_WordenGenegeerd()
    {
        var result = OntvangerParser.Parse("  a@voorbeeld.nl  ;   b@voorbeeld.nl  ");

        result.IsValid.Should().BeTrue();
        result.Emailadressen.Should().Equal("a@voorbeeld.nl", "b@voorbeeld.nl");
    }

    [Fact]
    public void Parse_DuplicaatAdres_WordtEenmaalGeteld()
    {
        var result = OntvangerParser.Parse("a@voorbeeld.nl; A@VOORBEELD.NL");

        result.IsValid.Should().BeTrue();
        result.Emailadressen.Should().Equal("a@voorbeeld.nl");
    }

    [Fact]
    public void Parse_NaamEnKaalAdresVoorZelfdePersoon_WordtSamengevoegd()
    {
        var result = OntvangerParser.Parse("\"Jan\" <a@voorbeeld.nl>; a@voorbeeld.nl");

        result.IsValid.Should().BeTrue();
        result.Emailadressen.Should().Equal("a@voorbeeld.nl");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Leeg_GeeftOngeldigMetUitleg(string? ruweRegel)
    {
        var result = OntvangerParser.Parse(ruweRegel);

        result.IsValid.Should().BeFalse();
        result.Emailadressen.Should().BeEmpty();
        result.FoutMelding.Should().Contain("minimaal één ontvanger");
    }

    [Fact]
    public void Parse_MeerDanVijftien_GeeftOngeldigMetAantal()
    {
        var adressen = Enumerable.Range(1, 16).Select(i => $"persoon{i}@voorbeeld.nl");
        var result = OntvangerParser.Parse(string.Join("; ", adressen));

        result.IsValid.Should().BeFalse();
        result.FoutMelding.Should().Contain("Maximaal 15 ontvangers");
        result.FoutMelding.Should().Contain("16");
    }

    [Fact]
    public void Parse_PreciesVijftien_WordtGeaccepteerd()
    {
        var adressen = Enumerable.Range(1, 15).Select(i => $"persoon{i}@voorbeeld.nl");
        var result = OntvangerParser.Parse(string.Join("; ", adressen));

        result.IsValid.Should().BeTrue();
        result.Emailadressen.Should().HaveCount(15);
    }

    [Theory]
    [InlineData("geen-emailadres")]
    [InlineData("iets@")]
    [InlineData("@voorbeeld.nl")]
    [InlineData("a@voorbeeld.nl; niet-geldig")]
    public void Parse_OngeldigeSyntax_GeeftOngeldigMetFragment(string ruweRegel)
    {
        var result = OntvangerParser.Parse(ruweRegel);

        result.IsValid.Should().BeFalse();
        result.FoutMelding.Should().Contain("Ongeldig e-mailadres");
    }

    [Fact]
    public void Parse_EersteOngeldigeAdresStoptVerwerking_NietStilOvergeslagen()
    {
        var result = OntvangerParser.Parse("geldig@voorbeeld.nl; niet-geldig; ook-geldig@voorbeeld.nl");

        result.IsValid.Should().BeFalse();
        result.FoutMelding.Should().Contain("niet-geldig");
    }
}
