using FluentAssertions;
using SportlinkFunction.TeamResolution;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

/// <summary>
/// Tests op basis van de faalscenario's uit de #692-analyse ÉN van de naamvormen die echt in
/// Sportlink voorkomen (geverifieerd tegen live stg.teams-data tijdens #696).
/// Clubcode in tests is bewust een neutrale placeholder — nooit een echte clubnaam.
/// </summary>
public class TeamNaamNormalisatieTests
{
    private const string Club = "TESTCLUB";

    // ── Bonds- vs lokale notatie: de kern van de ontdubbeling (#696) ──

    [Theory]
    [InlineData("JO10-1", "TESTCLUB O10-1")]   // lokaal (met J) vs KNVB (zonder J, met prefix)
    [InlineData("JO13-2", "TESTCLUB O13-2")]
    [InlineData("MO13-1", "TESTCLUB MO13-1")]  // meisjes: MO in beide notaties
    [InlineData("G-1", "TESTCLUB G1")]         // G-team: streepje lokaal, geen streepje bij bond
    [InlineData("1", "TESTCLUB 1")]            // senioren
    public void LokaleEnKnvbNotatie_LeverenDezelfdeSleutel(string lokaal, string bond)
    {
        var sleutelLokaal = TeamNaamNormalisatie.NormaliseerVoorVergelijking(lokaal, Club);
        var sleutelBond = TeamNaamNormalisatie.NormaliseerVoorVergelijking(bond, Club);

        sleutelBond.Should().Be(sleutelLokaal);
    }

    [Fact]
    public void MeisjesTeam_WordtNietDubbelGeprefixt()
    {
        // "MO13-1" mag nooit "JMO13-1" worden: de O staat al achter een letter.
        TeamNaamNormalisatie.NormaliseerVoorVergelijking("MO13-1", Club).Should().Be("MO13-1");
    }

    [Theory]
    [InlineData("TESTCLUB 35+1", "35+1")]        // veteranen: '+' blijft intact
    [InlineData("TESTCLUB VR30+1", "VR30+1")]    // vrouwen veteranen
    [InlineData("TESTCLUB VR1", "VR1")]          // vrouwen senioren
    [InlineData("TESTCLUB O14-1JM", "JO14-1JM")] // gemengd jongens/meiden, JM-suffix blijft
    public void EchteSportlinkVormen_BlijvenIntact(string invoer, string verwacht)
    {
        TeamNaamNormalisatie.NormaliseerVoorVergelijking(invoer, Club).Should().Be(verwacht);
    }

    [Fact]
    public void TeamMetEigenNaam_BlijftHerkenbaar()
    {
        // Sommige clubs geven mini-teams een eigen naam; die mag niet gemangeld worden.
        TeamNaamNormalisatie.NormaliseerVoorVergelijking("Spitsies", Club).Should().Be("SPITSIES");
    }

    [Fact]
    public void AlleenDeClubcode_WordtNietGestriptTotLegeSleutel()
    {
        TeamNaamNormalisatie.NormaliseerVoorVergelijking("TESTCLUB", Club).Should().Be("TESTCLUB");
    }

    [Fact]
    public void TegenstanderTeam_ZonderClubPrefixParameter_HoudtClubdeel()
    {
        // Bij een tegenstander is het clubdeel juist onderscheidend — dan geen prefix meegeven.
        TeamNaamNormalisatie.NormaliseerVoorVergelijking("Voorbeeld SV JO13-2").Should().Be("VOORBEELDSVJO13-2");
    }

    // ── Formatteringsvarianten uit e-mail (#692 scenario's 1-3, 19) ──

    [Theory]
    [InlineData("JO13-2", "JO13-2")]
    [InlineData("JO13/2", "JO13-2")]
    [InlineData("JO 13-2", "JO13-2")]
    [InlineData("jo13 - 2", "JO13-2")]
    [InlineData("jo13/2", "JO13-2")]
    [InlineData("JO13.2", "JO13-2")]
    [InlineData("JO13,2", "JO13-2")]
    [InlineData("jo 13 /2", "JO13-2")]
    public void FormatteringsVarianten_LeverenZelfdeSleutel(string input, string verwacht)
    {
        TeamNaamNormalisatie.NormaliseerVoorVergelijking(input, Club).Should().Be(verwacht);
    }

    // ── Spatie als scheidingsteken tussen leeftijd en teamnummer (#766) ──

    [Theory]
    [InlineData("MO13 1", "MO13-1")]
    [InlineData("JO13 2", "JO13-2")]
    [InlineData("TESTCLUB MO13 1", "MO13-1")]
    [InlineData("TESTCLUB O13 2", "JO13-2")]
    [InlineData("Onder 13 1", "JO13-1")]
    [InlineData("13 1", "13-1")]        // prefixloos: blijft prefixloos, wél met streepje
    [InlineData("MO 13 1", "MO13-1")]
    public void SpatieTussenLeeftijdEnTeamnummer_IsOokEenScheidingsteken(string input, string verwacht)
    {
        // Deze vorm komt in brondata én in e-mail voor. Zonder deze regel werd "MO13 1" de sleutel
        // "MO131" en "MO13-1" de sleutel "MO13-1" — twee sleutels voor hetzelfde team, waardoor de
        // teamherkenning en daarmee de hele wedstrijdlookup omvielen (#766).
        TeamNaamNormalisatie.NormaliseerVoorVergelijking(input, Club).Should().Be(verwacht);
    }

    [Fact]
    public void SpatieNotatie_LeverdDezelfdeSleutelAlsStreepjeNotatie()
    {
        var metSpatie = TeamNaamNormalisatie.NormaliseerVoorVergelijking("TESTCLUB MO13 1", Club);
        var metStreepje = TeamNaamNormalisatie.NormaliseerVoorVergelijking("MO13-1", Club);

        metSpatie.Should().Be(metStreepje);
    }

    [Fact]
    public void SpatieNotatie_WordtVolledigOntleed()
    {
        // Het gevolg van de gemiste sleutel was dat Parse() null gaf, waardoor LeeftijdNummer en
        // TeamNummer in dbo.Teams NULL bleven — en dáármee viel het kandidaten- en
        // disambiguatiepad stil voor elk team met deze notatie.
        var componenten = TeamNaamNormalisatie.Parse("TESTCLUB MO13 1", Club);

        componenten.Should().NotBeNull();
        componenten!.Prefix.Should().Be("MO");
        componenten.LeeftijdNummer.Should().Be(13);
        componenten.TeamNummer.Should().Be(1);
    }

    [Theory]
    [InlineData("TESTCLUB 35+ 1", "35+1")]   // veteranen: '+' is geen leeftijd/teamnummer-scheiding
    [InlineData("TESTCLUB VR 1", "VR1")]     // letter-categorie + nummer: geen streepje
    [InlineData("TESTCLUB 1", "1")]          // senioren: één nummer, niets te scheiden
    [InlineData("Heren 1", "HEREN1")]
    public void SpatieRegel_RaaktGeenVormenZonderTweeCijferreeksen(string input, string verwacht)
    {
        TeamNaamNormalisatie.NormaliseerVoorVergelijking(input, Club).Should().Be(verwacht);
    }

    [Theory]
    [InlineData("Onder 13", "JO13")]
    [InlineData("O13", "JO13")]
    [InlineData("o13", "JO13")]
    [InlineData("J013-2", "JO13-2")]   // scenario 10: cijfer 0 i.p.v. letter O
    [InlineData("MJ13-1", "JM13-1")]   // scenario 14: regionale volgorde-variant
    [InlineData("JO13-2 (2025-2026)", "JO13-2")] // scenario 21: seizoensaanduiding
    public void PrefixEnTypefoutNormalisatie(string input, string verwacht)
    {
        TeamNaamNormalisatie.NormaliseerVoorVergelijking(input, Club).Should().Be(verwacht);
    }

    [Fact]
    public void KaalNummerZonderPrefix_BlijftZonderPrefix()
    {
        // Scenario 4: "13-1" is een echte ambiguïteit (JO13-1 of MO13-1). De normalisatie mag
        // NOOIT zelf een prefix verzinnen — dat hoort bij de resolver/disambiguatie.
        TeamNaamNormalisatie.NormaliseerVoorVergelijking("13-1", Club).Should().Be("13-1");
    }

    [Fact]
    public void LegeOfNullInvoer_GeeftLegeString()
    {
        TeamNaamNormalisatie.NormaliseerVoorVergelijking(null, Club).Should().Be("");
        TeamNaamNormalisatie.NormaliseerVoorVergelijking("   ", Club).Should().Be("");
    }

    // ── LijktOpTeamPatroon ──

    [Theory]
    [InlineData("JO13-2", true)]
    [InlineData("13-1", true)]
    [InlineData("TESTCLUB G1", true)]
    [InlineData("TESTCLUB 35+1", true)]
    [InlineData("TESTCLUB VR30+1", true)]
    [InlineData("TESTCLUB O14-1JM", true)]
    [InlineData("Kan de wedstrijd verplaatst worden?", false)]
    [InlineData("Spitsies", false)]
    public void LijktOpTeamPatroon_OnderscheidtTeamtekstVanVrijeTekst(string input, bool verwacht)
    {
        TeamNaamNormalisatie.LijktOpTeamPatroon(input, Club).Should().Be(verwacht);
    }

    // ── Parse ──

    [Fact]
    public void Parse_VolledigTeamMetPrefix_OntleedtAlleComponenten()
    {
        var componenten = TeamNaamNormalisatie.Parse("JO13-2", Club);

        componenten.Should().NotBeNull();
        componenten!.Prefix.Should().Be("JO");
        componenten.LeeftijdNummer.Should().Be(13);
        componenten.TeamNummer.Should().Be(2);
    }

    [Fact]
    public void Parse_KnvbNotatie_OntleedtNaNormalisatieOokAlsJo()
    {
        var componenten = TeamNaamNormalisatie.Parse("TESTCLUB O13-2", Club);

        componenten.Should().NotBeNull();
        componenten!.Prefix.Should().Be("JO");
        componenten.LeeftijdNummer.Should().Be(13);
        componenten.TeamNummer.Should().Be(2);
    }

    [Fact]
    public void Parse_KaalNummer_PrefixIsNullMaarNummersZijnBekend()
    {
        var componenten = TeamNaamNormalisatie.Parse("13-1", Club);

        componenten.Should().NotBeNull();
        componenten!.Prefix.Should().BeNull();
        componenten.LeeftijdNummer.Should().Be(13);
        componenten.TeamNummer.Should().Be(1);
    }

    [Theory]
    [InlineData("TESTCLUB 1")]        // senioren: geen leeftijd+teamnummer
    [InlineData("TESTCLUB 35+1")]     // veteranen
    [InlineData("TESTCLUB VR1")]      // vrouwen
    [InlineData("TESTCLUB G1")]       // G-team
    [InlineData("Spitsies")]          // eigen naam
    [InlineData("Kan de wedstrijd verplaatst worden?")]
    public void Parse_VormenZonderLeeftijdEnTeamnummer_GevenNull(string input)
    {
        // Voor deze vormen is alleen een exacte match of gevalideerde alias correct — kandidaten
        // zoeken op nummer zou verkeerde treffers geven.
        TeamNaamNormalisatie.Parse(input, Club).Should().BeNull();
    }
}
