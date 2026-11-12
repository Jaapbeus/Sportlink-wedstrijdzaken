using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// Verhuisd uit <c>FunctionApp.Tests/Planner/LeeftijdNormalisatieTests.cs</c> (#889), samen met de
/// methode zelf. De SQL-generatie per tier heeft een eigen test in de betreffende boom.
/// </summary>
public class LeeftijdNormalisatieTests
{
    [Fact]
    public void Normaliseer_Senioren_MaptNaarEenTotNegenennegentig()
    {
        LeeftijdNormalisatie.Normaliseer("Senioren").Should().Be("1-99");
    }

    [Fact]
    public void Normaliseer_SeniorenVrouwen_MaptNaarVr()
    {
        LeeftijdNormalisatie.Normaliseer("Senioren Vrouwen").Should().Be("VR");
    }

    [Fact]
    public void Normaliseer_BestaandeJeugdFormaten_BlijvenOngewijzigd()
    {
        LeeftijdNormalisatie.Normaliseer("JO15 Meiden").Should().Be("MO15");
        LeeftijdNormalisatie.Normaliseer("Onder 13").Should().Be("JO13");
    }

    [Theory]
    [InlineData("MO13")]
    [InlineData("1-99")]
    [InlineData("VR")]
    [InlineData("JO13")]
    [InlineData("G")]
    public void Normaliseer_CanoniekeSleutel_IsIdempotent(string sleutel)
    {
        var eenKeer = LeeftijdNormalisatie.Normaliseer(sleutel);
        LeeftijdNormalisatie.Normaliseer(eenKeer).Should().Be(eenKeer);
        eenKeer.Should().Be(sleutel);
    }

    /// <summary>
    /// #1332: Sportlink levert voor sommige meisjesteams "Onder {n} Meiden" i.p.v. "{JO|MO}{n} Meiden".
    /// De vorige implementatie nam aan dat het cijfer al direct na "JO"/"MO" stond en leverde voor dit
    /// format de niet-bestaande sleutel "MOOnder 13" op, waardoor de speeltijd-lookup faalde en de
    /// wedstrijd zonder foutmelding uit de Dagplanning-Gantt verdween.
    /// </summary>
    [Theory]
    [InlineData("Onder 13 Meiden", "MO13")]
    [InlineData("Onder 11 Meiden", "MO11")]
    [InlineData("Onder 15 Meiden", "MO15")]
    [InlineData("Onder 17 Meiden", "MO17")]
    [InlineData("Onder 9 Meiden", "MO9")]
    public void Normaliseer_VolwoordVariantMetMeidenErachter_MaptNaarMoN(string invoer, string verwacht)
    {
        LeeftijdNormalisatie.Normaliseer(invoer).Should().Be(verwacht);
    }

    [Theory]
    [InlineData("MO 13", "MO13")]
    [InlineData("MOJO13", "MO13")]
    public void Normaliseer_ExplicietMoPrefix_BehoudtBestaandeUitkomst(string invoer, string verwacht)
    {
        LeeftijdNormalisatie.Normaliseer(invoer).Should().Be(verwacht);
    }

    [Fact]
    public void Normaliseer_MeisjesOnderN_MaptNaarMoN()
    {
        // Voorheen leverde de Replace-keten voor dit format "MOJO15" op i.p.v. "MO15": de eerste
        // Replace("Onder ", "JO") gaf al "Meisjes JO15" vóór de tweede Replace("Meisjes ", "MO") kon
        // toeslaan.
        LeeftijdNormalisatie.Normaliseer("Meisjes Onder 15").Should().Be("MO15");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normaliseer_LegeInvoer_LevertLegeSleutel(string? invoer, string verwacht)
    {
        LeeftijdNormalisatie.Normaliseer(invoer).Should().Be(verwacht);
    }

    private static Dictionary<string, Speeltijd> Rijen(int jo23, int senioren = 115) => new()
    {
        ["JO23"] = new Speeltijd { Leeftijd = "JO23", WedstrijdTotaal = jo23, Veldafmeting = 1.00m },
        ["1-99"] = new Speeltijd { Leeftijd = "1-99", WedstrijdTotaal = senioren, Veldafmeting = 1.00m },
        ["VR"] = new Speeltijd { Leeftijd = "VR", WedstrijdTotaal = senioren, Veldafmeting = 1.00m },
    };

    [Theory]
    [InlineData("1-99", "TESTCLUB O23-1", "JO23")]   // #1863: Sportlink levert "Senioren", de speeltijd is die van JO23
    [InlineData("1-99", "TESTCLUB JO23-2", "JO23")]
    [InlineData("1-99", "TESTCLUB 1", "1-99")]       // eerste elftal houdt seniorenduur
    [InlineData("VR", "TESTCLUB VR1", "VR")]         // vrouwen houden hun sleutel
    [InlineData("1-99", "TESTCLUB 35+1", "1-99")]    // veteranen
    [InlineData("MO15", "TESTCLUB MO23-1", "MO15")]  // MO23 is geen O23
    [InlineData("1-99", null, "1-99")]
    [InlineData(null, "TESTCLUB 1", "")]
    public void SpeeltijdSleutel_O23WintVanSeniorencategorie(string? categorie, string? team, string verwacht)
        => LeeftijdNormalisatie.SpeeltijdSleutel(categorie, team, Rijen(105)).Should().Be(verwacht);

    [Fact]
    public void SpeeltijdSleutel_O23ZonderBruikbareJo23Rij_ValtTerugOpBronCategorie()
    {
        // Rij zonder duur (0) of ontbrekende rij: nooit duur 0 door deze regel.
        LeeftijdNormalisatie.SpeeltijdSleutel("1-99", "TESTCLUB O23-1", Rijen(0)).Should().Be("1-99");
        var zonderRij = Rijen(105); zonderRij.Remove("JO23");
        LeeftijdNormalisatie.SpeeltijdSleutel("1-99", "TESTCLUB O23-1", zonderRij).Should().Be("1-99");
    }

    [Theory]
    [InlineData("Senioren", "1-99")]           // #1863: eerste elftal en veteranen
    [InlineData("1-99", "1-99")]
    [InlineData("Senioren Vrouwen", "VR")]
    [InlineData("VR", "VR")]
    [InlineData("JO13", null)]                 // jeugd: geen seniorensleutel
    [InlineData("MO15", null)]
    [InlineData("G", null)]
    [InlineData("Veteranen", null)]            // onbekende vorm: geen gok
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SeniorenSpeeltijdSleutel_AlleenVoorSeniorencategorieen(string? categorie, string? verwacht)
        => LeeftijdNormalisatie.SeniorenSpeeltijdSleutel(categorie).Should().Be(verwacht);
}
