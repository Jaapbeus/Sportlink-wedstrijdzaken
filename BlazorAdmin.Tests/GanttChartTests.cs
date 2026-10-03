using AwesomeAssertions;
using BlazorAdmin.Shared;
using Xunit;
using static BlazorAdmin.Services.DagplanningWeergaveHelpers;

namespace BlazorAdmin.Tests;

/// <summary>
/// De tijdas- en veldvolgordelogica van <see cref="GanttChart"/> (#1491). BlazorAdmin.Tests gebruikt
/// geen bUnit, dus de render zelf is hier niet gedekt — alleen de pure berekeningen die voorheen
/// dubbel in de twee pagina's stonden.
/// </summary>
public class GanttChartTests
{
    private static GanttItem Item(string veld, string aanvang, int duurMin)
    {
        var start = TimeOnly.Parse(aanvang);
        return new GanttItem(veld, null, start, start.AddMinutes(duurMin), 1m, "A - B", "ongewijzigd", duurMin, null, null);
    }

    [Fact]
    public void BerekenTijdAs_RondtAfOpHeleUren()
    {
        var (start, eind, totaal) = GanttChart.BerekenTijdAs([Item("Veld 1", "09:30", 90), Item("Veld 2", "11:00", 75)]);

        start.Should().Be(9 * 60);
        eind.Should().Be(13 * 60); // laatste einde 12:15 -> 13:00
        totaal.Should().Be(eind - start);
    }

    [Fact]
    public void BerekenTijdAs_EindOpHeleUur_BlijftStaan()
    {
        var (start, eind, totaal) = GanttChart.BerekenTijdAs([Item("Veld 1", "10:00", 60)]);

        (start, eind, totaal).Should().Be((600, 660, 60));
    }

    [Fact]
    public void OrdenVelden_KunstgrasEerst_DaarnaAlfabetisch_ZonderDubbelen()
    {
        var velden = GanttChart.OrdenVelden([Item("Gras 2", "10:00", 60), Item("Kunstgras 1", "10:00", 60),
            Item("Gras 1", "11:00", 60), Item("Kunstgras 1", "12:00", 60)]);

        velden.Should().Equal("Kunstgras 1", "Gras 1", "Gras 2");
    }
}
