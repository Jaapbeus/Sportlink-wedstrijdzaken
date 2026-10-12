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
}
