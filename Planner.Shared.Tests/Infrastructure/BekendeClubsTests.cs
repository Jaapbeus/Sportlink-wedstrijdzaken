using AwesomeAssertions;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

/// <summary>Regressietests voor de gecachete clublijst waaraan de clubcode wordt getoetst (#1677).</summary>
public class BekendeClubsTests
{
    private static readonly TimeSpan Wacht = TimeSpan.FromSeconds(10);
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
        clubs.WachtOpVerversing(Wacht).Should().BeTrue();
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
        clubs.WachtOpVerversing(Wacht).Should().BeTrue();
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

    [Fact]
    public void VerlopenLijst_GeeftStaleDirect_EnVerverstOpDeAchtergrond()
    {
        var nu = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var gestart = new ManualResetEventSlim();
        using var vrij = new ManualResetEventSlim();
        var aanroepen = 0;
        var clubs = new BekendeClubs(() =>
        {
            if (Interlocked.Increment(ref aanroepen) == 1) return ["OUD"];
            gestart.Set();
            vrij.Wait(Wacht);
            return ["NIEUW"];
        }, TimeSpan.FromSeconds(10), () => nu);
        clubs.Zoek("OUD").Should().Be("OUD");

        nu = nu.AddSeconds(11);
        clubs.Zoek("OUD").Should().Be("OUD");            // stale, de verversing hangt nog
        gestart.Wait(Wacht).Should().BeTrue();
        clubs.Zoek("NIEUW").Should().BeNull();
        aanroepen.Should().Be(2);                         // geen tweede verversing tijdens de eerste

        vrij.Set();
        clubs.WachtOpVerversing(Wacht).Should().BeTrue();
        clubs.Zoek("NIEUW").Should().Be("NIEUW");
    }

    [Fact]
    public void MislukteVerversing_LaatOudeLijstStaan_EnProbeertNietDirectOpnieuw()
    {
        var nu = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var aanroepen = 0;
        var clubs = new BekendeClubs(() =>
        {
            if (Interlocked.Increment(ref aanroepen) == 1) return Lijst;
            throw new InvalidOperationException();
        }, TimeSpan.FromSeconds(10), () => nu);
        clubs.Zoek("ALLSTARS");

        nu = nu.AddSeconds(11);
        clubs.Zoek("ALLSTARS");
        clubs.WachtOpVerversing(Wacht).Should().BeTrue();
        clubs.Zoek("ALLSTARS").Should().Be("ALLSTARS");
        aanroepen.Should().Be(2);
    }

    [Fact]
    public void EersteLadingHangt_GeeftNaTimeoutOnbekend_ZonderTeBlijvenWachten()
    {
        using var vrij = new ManualResetEventSlim();
        var clubs = new BekendeClubs(() => { vrij.Wait(Wacht); return Lijst; },
            eersteLadingTimeout: TimeSpan.FromMilliseconds(50));

        clubs.Zoek("ALLSTARS").Should().BeNull();

        vrij.Set();
        clubs.WachtOpVerversing(Wacht).Should().BeTrue();
        clubs.Zoek("ALLSTARS").Should().Be("ALLSTARS");
    }

    [Fact]
    public void GelijktijdigeEersteLezers_VeroorzakenEenLading_EnAndereLezersKrijgenOnbekend()
    {
        using var gestart = new ManualResetEventSlim();
        using var vrij = new ManualResetEventSlim();
        var aanroepen = 0;
        var clubs = new BekendeClubs(() =>
        {
            Interlocked.Increment(ref aanroepen);
            gestart.Set();
            vrij.Wait(Wacht);
            return Lijst;
        }, eersteLadingTimeout: TimeSpan.FromSeconds(10));

        var lader = Task.Run(() => clubs.Zoek("ALLSTARS"));
        gestart.Wait(Wacht).Should().BeTrue();

        clubs.Zoek("ALLSTARS").Should().BeNull();         // wacht niet op de lader
        vrij.Set();
        lader.Wait(Wacht).Should().BeTrue();
        lader.Result.Should().Be("ALLSTARS");
        aanroepen.Should().Be(1);
    }
}
