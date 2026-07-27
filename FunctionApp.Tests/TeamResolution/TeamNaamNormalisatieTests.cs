using FluentAssertions;
using SportlinkFunction.TeamResolution;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

/// <summary>
/// Regressietests op basis van de 25 faalscenario's uit de #692-analyse (spatie/streepje-varianten,
/// ontbrekend prefix, typefouts, case-gevoeligheid, seizoensaanduidingen).
/// </summary>
public class TeamNaamNormalisatieTests
{
    [Theory]
    [InlineData("JO13-2", "JO13-2")]
    [InlineData("JO13/2", "JO13-2")] // scenario 1: slash-variant
    [InlineData("JO 13-2", "JO13-2")] // scenario 1: spatie voor streepje
    [InlineData("jo13 - 2", "JO13-2")] // scenario 1+2: lowercase + spaties rond streepje
    [InlineData("jo13/2", "JO13-2")] // scenario 2: lowercase + slash
    [InlineData("JO13.2", "JO13-2")] // scenario 3: punt i.p.v. streepje
    [InlineData("JO13,2", "JO13-2")] // komma-variant
    public void NormaliseerVoorVergelijking_FormatteringsVarianten_LeverenZelfdeSleutelOp(string input, string verwacht)
    {
        TeamNaamNormalisatie.NormaliseerVoorVergelijking(input).Should().Be(verwacht);
    }

    [Fact]
    public void NormaliseerVoorVergelijking_KaleLeeftijdEnNummerZonderPrefix_BlijftZonderPrefix()
    {
        // Scenario 4: "13-1" zonder JO/MO is een echte ambiguïteit — de normalisatie mag
        // NOOIT zelf een prefix verzinnen, dat hoort bij de resolver/disambiguatie.
        TeamNaamNormalisatie.NormaliseerVoorVergelijking("13-1").Should().Be("13-1");
    }

    [Fact]
    public void NormaliseerVoorVergelijking_TegenstanderZonderSpatie_WordtNietVeranderdDoorNormalisatie()
    {
        // Scenario 5: de vroegere "geen spatie = eigen team"-heuristiek zat in BerichtPipeline,
        // niet in normalisatie. Deze klasse doet geen eigen/tegenstander-uitspraak — bevestigt
        // dat de nieuwe architectuur die concern losgekoppeld heeft.
        TeamNaamNormalisatie.NormaliseerVoorVergelijking("AjaxJO13-2").Should().Be("AJAXJO13-2");
    }

    [Theory]
    [InlineData("Onder 13", "JO13")]
    [InlineData("O13", "JO13")]
    [InlineData("o13", "JO13")]
    [InlineData("MO13", "MO13")] // scenario 2: mag niet dubbel geprefixt worden tot JMO13
    public void NormaliseerVoorVergelijking_PrefixNormalisatie(string input, string verwacht)
    {
        TeamNaamNormalisatie.NormaliseerVoorVergelijking(input).Should().Be(verwacht);
    }

    [Fact]
    public void NormaliseerVoorVergelijking_TypefoutCijferNulVoorLetterO_WordtHersteld()
    {
        // Scenario 10: "J013-2" (cijfer 0 i.p.v. letter O)
        TeamNaamNormalisatie.NormaliseerVoorVergelijking("J013-2").Should().Be("JO13-2");
    }

    [Theory]
    [InlineData("MJ13-1", "JM13-1")] // scenario 14: regionale volgorde-variant
    [InlineData("JM13-1", "JM13-1")]
    public void NormaliseerVoorVergelijking_JmMjVolgordeVariant(string input, string verwacht)
    {
        TeamNaamNormalisatie.NormaliseerVoorVergelijking(input).Should().Be(verwacht);
    }

    [Fact]
    public void NormaliseerVoorVergelijking_SeizoensaanduidingInHaakjes_WordtGestript()
    {
        // Scenario 21: "JO13-2 (2025-2026)"
        TeamNaamNormalisatie.NormaliseerVoorVergelijking("JO13-2 (2025-2026)").Should().Be("JO13-2");
    }

    [Fact]
    public void NormaliseerVoorVergelijking_GestapeldeSlordigheid_LeidtTochTotSchoneSleutel()
    {
        // Scenario 19: spatie + slash + lowercase tegelijk
        TeamNaamNormalisatie.NormaliseerVoorVergelijking("jo 13 /2").Should().Be("JO13-2");
    }

    [Fact]
    public void NormaliseerVoorVergelijking_LegeOfNullInvoer_GeeftLegeString()
    {
        TeamNaamNormalisatie.NormaliseerVoorVergelijking(null).Should().Be("");
        TeamNaamNormalisatie.NormaliseerVoorVergelijking("   ").Should().Be("");
    }

    [Theory]
    [InlineData("JO13-2", true)]
    [InlineData("13-1", true)]
    [InlineData("G1", true)]
    [InlineData("Kan de wedstrijd verplaatst worden?", false)]
    public void LijktOpTeamPatroon_OnderscheidtTeamtekstVanVrijeTekst(string input, bool verwacht)
    {
        TeamNaamNormalisatie.LijktOpTeamPatroon(input).Should().Be(verwacht);
    }

    [Fact]
    public void Parse_VolledigTeamMetPrefix_OntleedtAlleComponenten()
    {
        var componenten = TeamNaamNormalisatie.Parse("JO13-2");

        componenten.Should().NotBeNull();
        componenten!.Prefix.Should().Be("JO");
        componenten.LeeftijdNummer.Should().Be(13);
        componenten.TeamNummer.Should().Be(2);
    }

    [Fact]
    public void Parse_KaalNummerZonderPrefix_PrefixIsNullMaarNummersZijnBekend()
    {
        // Scenario 4: ambiguïteit blijft zichtbaar in de output (Prefix == null),
        // in plaats van stilzwijgend te raden.
        var componenten = TeamNaamNormalisatie.Parse("13-1");

        componenten.Should().NotBeNull();
        componenten!.Prefix.Should().BeNull();
        componenten.LeeftijdNummer.Should().Be(13);
        componenten.TeamNummer.Should().Be(1);
    }

    [Fact]
    public void Parse_NietTeamAchtigeTekst_GeeftNull()
    {
        TeamNaamNormalisatie.Parse("Kan de wedstrijd verplaatst worden?").Should().BeNull();
    }
}
