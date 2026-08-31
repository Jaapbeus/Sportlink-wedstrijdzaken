using FluentAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// Legt het gedrag van <see cref="AvgMaskering"/> vast (#858).
///
/// <para>
/// <b>De bug die deze tests bewaken.</b> De maskering hing aan een letterlijke, hoofdlettergevoelige
/// opzoeking op <c>"Afzender"</c>. Onder de lowercase-conventie van een niet-SQL-Server-tier heet de
/// kolom <c>afzender</c>; de opzoeking miste dan, en het <b>volledige e-mailadres</b> ging
/// onvermaskerd naar de browser — zonder foutmelding, zonder logregel, zonder dat iemand het merkte.
/// Dat is een AVG-schending die zich als "werkt gewoon" voordoet.
/// </para>
/// </summary>
public class AvgMaskeringTests
{
    [Theory]
    [InlineData("Afzender")]
    [InlineData("afzender")]
    [InlineData("AFZENDER")]
    public void MaskeerAfzender_VindtDeKolomOngeachtHoofdlettergebruik(string kolomNaam)
    {
        var rij = new Dictionary<string, object?> { [kolomNaam] = "iemand@voorbeeld.nl" };

        AvgMaskering.MaskeerAfzender(rij);

        rij[kolomNaam].Should().Be("***@voorbeeld.nl",
            "de lowercase-variant is precies het geval waarin de oude, ordinale opzoeking stil faalde");
    }

    [Fact]
    public void MaskeerAfzender_BehoudtHetDomeinMaarNooitDeGebruikersnaam()
    {
        var rij = new Dictionary<string, object?> { ["Afzender"] = "voornaam.achternaam@voorbeeld.nl" };

        AvgMaskering.MaskeerAfzender(rij);

        rij["Afzender"].Should().Be("***@voorbeeld.nl");
        rij["Afzender"]!.ToString().Should().NotContain("voornaam");
    }

    [Fact]
    public void MaskeerAfzender_AdresZonderApenstaartje_WordtVolledigVervangen()
    {
        var rij = new Dictionary<string, object?> { ["Afzender"] = "onzin-zonder-at" };

        AvgMaskering.MaskeerAfzender(rij);

        rij["Afzender"].Should().Be("***",
            "zonder domein valt er niets veilig te tonen, dus gaat alles weg");
    }

    [Fact]
    public void MaskeerAfzender_LegeOfNullWaarde_BlijftOngewijzigd()
    {
        var leeg = new Dictionary<string, object?> { ["Afzender"] = null };
        AvgMaskering.MaskeerAfzender(leeg);
        leeg["Afzender"].Should().BeNull("er valt niets te lekken, dus ook niets te maskeren");
    }

    [Fact]
    public void MaskeerAfzender_KolomOntbreekt_Gooit()
    {
        // Het tweede stille faalpad uit #858: wie de SELECT aanpast en het alias laat vallen, kreeg
        // geen enkele melding — TryGetValue gaf false en de rij ging ongemaskeerd door. Nu knalt het.
        var rij = new Dictionary<string, object?> { ["Onderwerp"] = "Vraag over veldindeling" };

        var act = () => AvgMaskering.MaskeerAfzender(rij);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Afzender*",
                "een maskeerstap die niets vond is een fout in de query, geen no-op");
    }

    [Fact]
    public void Maskeer_IsDirectBruikbaarZonderDictionary()
    {
        AvgMaskering.Maskeer("iemand@voorbeeld.nl").Should().Be("***@voorbeeld.nl");
        AvgMaskering.Maskeer(null).Should().BeNull();
        AvgMaskering.Maskeer("  ").Should().Be("  ");
    }
}
