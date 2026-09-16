using BlazorAdmin.Services;
using AwesomeAssertions;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// Regressietests voor #1194: een halve-veld-boeking (bijv. "veld 3 A") werd als heel veld
/// getoond omdat de balkhoogte uit een losstaande teaminstelling kwam terwijl de verticale positie
/// al uit de subpositie-suffix kwam — twee incompatibele bronnen voor dezelfde balk.
/// </summary>
public class GanttLayoutTests
{
    [Theory]
    [InlineData("A", 0, 50)]
    [InlineData("B", 50, 50)]
    [InlineData("A1", 0, 25)]
    [InlineData("A2", 25, 25)]
    [InlineData("B1", 50, 25)]
    [InlineData("B2", 75, 25)]
    public void BekendeSubpositie_BepaaltTopEnHoogteOnafhankelijkVanDeServerfractie(
        string subpositie, double verwachtTop, double verwachtHoogte)
    {
        // Fractie 1.00m (heel veld, precies het #1194-scenario: een team met "heel veld" als
        // standaardinstelling dat ad-hoc een halve-veld-boeking kreeg) mag de uitkomst niet
        // beïnvloeden zolang Sportlink zelf een subpositie meegeeft.
        var (top, hoogte) = GanttLayout.Bereken(subpositie, fractieTerugval: 1.00m);

        top.Should().Be(verwachtTop);
        hoogte.Should().Be(verwachtHoogte);
    }

    [Theory]
    [InlineData(0.20, 25)]
    [InlineData(0.50, 50)]
    [InlineData(1.00, 100)]
    public void GeenSubpositie_ValtTerugOpDeServerfractie(double fractie, double verwachteHoogte)
    {
        var (top, hoogte) = GanttLayout.Bereken(subpositie: null, fractieTerugval: (decimal)fractie);

        top.Should().Be(0, "zonder subpositie is er geen basis voor een verticale verschuiving");
        hoogte.Should().Be(verwachteHoogte);
    }

    /// <summary>
    /// De defensieve clamp (#1194, stap 4 uit de issue): ongeacht welke combinatie van top/hoogte
    /// een toekomstige databron zou opleveren, mag de som nooit boven de 100% van de rijhoogte
    /// uitkomen — een balk mag nooit in de volgende veldrij doorlopen.
    /// </summary>
    [Fact]
    public void TopPlusHoogte_OverschrijdtNooitHonderdProcent()
    {
        // B2 → top 75; zonder clamp zou een (hypothetische) volle-veld-hoogte van 100 de balk tot
        // 175% van de rijhoogte laten doorlopen. Dit is precies het gerapporteerde symptoom.
        var (top, hoogte) = GanttLayout.Bereken("B2", fractieTerugval: 1.00m);

        (top + hoogte).Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void OnbekendeSubpositie_GedraagtZichAlsGeenSubpositie()
    {
        var (top, hoogte) = GanttLayout.Bereken("C", fractieTerugval: 0.50m);

        top.Should().Be(0);
        hoogte.Should().Be(50);
    }
}
