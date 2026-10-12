using AwesomeAssertions;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>#1647: admin ziet alles; een andere rol alleen de teruggegeven sleutels; DEBUG/Rechten per rol zijn admin-only.</summary>
public class MenuZichtbaarheidTests
{
    [Theory]
    [InlineData(MenuKeys.Dashboard)]
    [InlineData(MenuKeys.Thema)]
    [InlineData(MenuKeys.AppInstellingen)]
    public void Admin_ZietAlles_OokZonderSleutels(string sleutel) =>
        new MenuZichtbaarheid(true, null).Mag(sleutel).Should().BeTrue();

    [Fact]
    public void NietAdmin_ZietAlleenDeTeruggegevenSleutels()
    {
        var z = new MenuZichtbaarheid(false, [MenuKeys.Planning, MenuKeys.Speeltijden]);

        z.Mag(MenuKeys.Planning).Should().BeTrue();
        z.Mag(MenuKeys.Speeltijden).Should().BeTrue();
        z.Mag(MenuKeys.Teambegeleiding).Should().BeFalse();
        z.Mag(MenuKeys.Thema).Should().BeFalse();
    }

    [Fact]
    public void NietAdmin_ZonderLading_ZietAlleenHetDashboard()
    {
        var z = new MenuZichtbaarheid(false, null);

        z.Mag(MenuKeys.Dashboard).Should().BeTrue();
        z.Mag(MenuKeys.Planning).Should().BeFalse();
        z.Mag(MenuKeys.AppInstellingen).Should().BeFalse();
    }

    [Fact]
    public void NietAdmin_MetLegeLijst_ZietNiets()
    {
        var z = new MenuZichtbaarheid(false, []);

        z.Mag(MenuKeys.Dashboard).Should().BeFalse();
        z.MagEen(MenuKeys.Planning, MenuKeys.Velden).Should().BeFalse();
    }

    [Fact]
    public void Groep_IsZichtbaarAlsMinstensEenItemMagWorden()
    {
        var z = new MenuZichtbaarheid(false, [MenuKeys.Velden]);

        z.MagEen(MenuKeys.Speeltijden, MenuKeys.Velden, MenuKeys.Voorkeurstijden).Should().BeTrue();
        z.MagEen(MenuKeys.Thema, MenuKeys.Teamaliassen).Should().BeFalse();
    }
}
