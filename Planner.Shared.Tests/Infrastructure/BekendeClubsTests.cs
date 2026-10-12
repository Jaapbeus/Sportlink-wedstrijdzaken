using AwesomeAssertions;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

/// <summary>Regressietests voor de gecachete clublijst waaraan de clubcode wordt getoetst (#1677).</summary>
public class BekendeClubsTests
{
    private static readonly string[] Lijst = ["ALLSTARS", "VOORBEELD"];

    [Fact]
    public void BekendeCode_GeeftOpgeslagenWaarde_OnafhankelijkVanHoofdletters()
    {
        var clubs = new BekendeClubs(() => Lijst);

        clubs.Zoek("ALLSTARS").Should().Be("ALLSTARS");
        clubs.Zoek("allstars").Should().Be("ALLSTARS");
    }

    [Theory]
    [InlineData("ONBEKEND")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void OnbekendeOfLegeCode_GeeftNull(string? code)
        => new BekendeClubs(() => Lijst).Zoek(code).Should().BeNull();

    [Fact]
    public void OngeldigeVorm_IsNooitBekend_ZelfsAlsDeLijstHemBevat()
        => new BekendeClubs(() => ["AB CD", "X'; --"]).Zoek("AB CD").Should().BeNull();

    [Fact]
    public void DatabaseFout_GeeftNull_ZonderUitzondering()
    {
        var clubs = new BekendeClubs(() => throw new InvalidOperationException("db weg"));

        clubs.Zoek("ALLSTARS").Should().BeNull();
    }

    [Fact]
    public void DatabaseFout_NaEerdereLading_BehoudtLaatsteLijst()
    {
        var nu = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var faal = false;
        var clubs = new BekendeClubs(() => faal ? throw new InvalidOperationException() : Lijst,
            TimeSpan.FromSeconds(10), () => nu);
        clubs.Zoek("ALLSTARS").Should().Be("ALLSTARS");

        faal = true;
        nu = nu.AddSeconds(11);

        clubs.Zoek("ALLSTARS").Should().Be("ALLSTARS");
    }

    [Fact]
    public void Lijst_WordtGecachet_EnNaGeldigheidVerversd()
    {
        var nu = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var aanroepen = 0;
        var clubs = new BekendeClubs(() => { aanroepen++; return Lijst; }, TimeSpan.FromSeconds(10), () => nu);

        clubs.Zoek("ALLSTARS");
        clubs.Zoek("VOORBEELD");
        aanroepen.Should().Be(1);

        nu = nu.AddSeconds(11);
        clubs.Zoek("ALLSTARS");
        aanroepen.Should().Be(2);
    }

    [Fact]
    public void NaFout_WordtNietBijElkeAanroepOpnieuwGeprobeerd()
    {
        var nu = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var aanroepen = 0;
        var clubs = new BekendeClubs(() => { aanroepen++; throw new InvalidOperationException(); },
            TimeSpan.FromSeconds(60), () => nu);

        clubs.Zoek("A"); clubs.Zoek("B"); clubs.Zoek("C");

        aanroepen.Should().Be(1);
    }
}
