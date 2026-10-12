using AwesomeAssertions;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

/// <summary>#1671: de gedeelde glijdend-venster-limiet per gebruiker.</summary>
public class GebruikerLimietTests
{
    private static readonly DateTime Start = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BovenDeLimiet_WordtGeweigerd()
    {
        var limiet = new GebruikerLimiet(3, TimeSpan.FromMinutes(10), () => Start);

        limiet.TryAcquire("gebruiker-a").Should().BeTrue();
        limiet.TryAcquire("gebruiker-a").Should().BeTrue();
        limiet.TryAcquire("gebruiker-a").Should().BeTrue();
        limiet.TryAcquire("gebruiker-a").Should().BeFalse();
    }

    [Fact]
    public void Gebruikers_TellenAfzonderlijk()
    {
        var limiet = new GebruikerLimiet(1, TimeSpan.FromMinutes(10), () => Start);

        limiet.TryAcquire("gebruiker-a").Should().BeTrue();
        limiet.TryAcquire("gebruiker-b").Should().BeTrue();
        limiet.TryAcquire("gebruiker-a").Should().BeFalse();
    }

    [Fact]
    public void NaHetVenster_IsDeRuimteWeerVrij()
    {
        var nu = Start;
        var limiet = new GebruikerLimiet(1, TimeSpan.FromMinutes(10), () => nu);

        limiet.TryAcquire("gebruiker-a").Should().BeTrue();
        nu = Start.AddMinutes(11);
        limiet.TryAcquire("gebruiker-a").Should().BeTrue();
    }

    [Fact]
    public void LegeSleutel_WordtGezamenlijkBegrensd()
    {
        var limiet = new GebruikerLimiet(1, TimeSpan.FromMinutes(10), () => Start);

        limiet.TryAcquire(null).Should().BeTrue();
        limiet.TryAcquire("  ").Should().BeFalse();
    }

    [Fact]
    public void Opruiming_BehoudtDeTellerVanEenLopendeLimiet()
    {
        var nu = Start;
        var limiet = new GebruikerLimiet(3, TimeSpan.FromMinutes(10), () => nu);

        limiet.TryAcquire("gebruiker-a").Should().BeTrue();   // t0
        nu = Start.AddMinutes(6);
        limiet.TryAcquire("gebruiker-a").Should().BeTrue();   // t0+6
        limiet.TryAcquire("gebruiker-a").Should().BeTrue();   // t0+6
        nu = Start.AddMinutes(11);                             // t0 is verlopen, de twee van t0+6 zijn nog geldig

        // Veel andere sleutels laten de opruiming draaien.
        for (var i = 0; i < 600; i++) limiet.TryAcquire($"gebruiker-{i}").Should().BeTrue();

        limiet.TryAcquire("gebruiker-a").Should().BeTrue();    // precies één plek vrij (2 van 3 bezet)
        limiet.TryAcquire("gebruiker-a").Should().BeFalse();   // de teller is niet gereset
    }

    [Fact]
    public void Sleutels_ZijnNietHoofdlettergevoelig()
    {
        var limiet = new GebruikerLimiet(1, TimeSpan.FromMinutes(10), () => Start);

        limiet.TryAcquire("AbC-123").Should().BeTrue();
        limiet.TryAcquire("abc-123").Should().BeFalse();
    }

    [Fact]
    public void TerugGeven_MaaktEenSlotWeerVrij()
    {
        var limiet = new GebruikerLimiet(1, TimeSpan.FromMinutes(10), () => Start);

        limiet.TryAcquire("gebruiker-a").Should().BeTrue();
        limiet.TerugGeven("Gebruiker-A");

        limiet.TryAcquire("gebruiker-a").Should().BeTrue();
        limiet.TryAcquire("gebruiker-a").Should().BeFalse();
    }

    [Fact]
    public void TerugGeven_GeeftAlleenHetLaatsteSlotTerug()
    {
        var limiet = new GebruikerLimiet(2, TimeSpan.FromMinutes(10), () => Start);
        limiet.TryAcquire("gebruiker-a");
        limiet.TryAcquire("gebruiker-a");

        limiet.TerugGeven("gebruiker-a");

        limiet.TryAcquire("gebruiker-a").Should().BeTrue();
        limiet.TryAcquire("gebruiker-a").Should().BeFalse();
    }

    [Fact]
    public void TerugGeven_BijOnbekendeSleutelOfLegeTeller_HeeftGeenEffect()
    {
        var limiet = new GebruikerLimiet(1, TimeSpan.FromMinutes(10), () => Start);

        limiet.TerugGeven("onbekende-sleutel");
        limiet.TerugGeven(null);
        limiet.TryAcquire("gebruiker-a").Should().BeTrue();
        limiet.TerugGeven("gebruiker-a");
        limiet.TerugGeven("gebruiker-a");

        limiet.TryAcquire("gebruiker-a").Should().BeTrue();
        limiet.TryAcquire("gebruiker-a").Should().BeFalse();
        limiet.TryAcquire("gebruiker-b").Should().BeTrue();
    }
}
