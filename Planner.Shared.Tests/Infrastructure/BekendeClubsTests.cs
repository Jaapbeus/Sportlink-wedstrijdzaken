using AwesomeAssertions;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

/// <summary>Regressietests voor de gecachete clublijst waaraan de clubcode wordt getoetst (#1677).</summary>
public class BekendeClubsTests
{
    private static readonly TimeSpan Wacht = TimeSpan.FromSeconds(10);
    private static readonly string[] Lijst = ["ALLSTARS", "VOORBEELD"];
    private static readonly ClubZoekResultaat Allstars = ClubZoekResultaat.Bekend("ALLSTARS");
    private static readonly ClubZoekResultaat Onbekend = ClubZoekResultaat.Onbekend;

    private static BekendeClubs NietBeschikbaar() => new(() => throw new InvalidOperationException("db weg"));

    [Fact]
    public void BekendeCode_GeeftOpgeslagenWaarde_OnafhankelijkVanHoofdletters()
    {
        var clubs = new BekendeClubs(() => Lijst);

        clubs.Zoek("ALLSTARS").Should().Be(Allstars);
        clubs.Zoek("allstars").Should().Be(Allstars);
    }

    [Theory]
    [InlineData("ONBEKEND")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void OnbekendeOfLegeCode_IsOnbekend(string? code)
        => new BekendeClubs(() => Lijst).Zoek(code).Should().Be(Onbekend);

    [Fact]
    public void OngeldigeVorm_IsNooitBekend_ZelfsAlsDeLijstHemBevat()
        => new BekendeClubs(() => ["AB CD", "X'; --"]).Zoek("AB CD").Should().Be(Onbekend);

    // Keuzelogica van EasyAuthHelper.GetClubCodeFromRequest: CodeVoorVerzoek == null ⇒ primaire club.
    [Theory]
    [InlineData("lijst", "allstars", "ALLSTARS")]   // Bekend ⇒ opgeslagen schrijfwijze
    [InlineData("lijst", "ONBEKEND", null)]         // Onbekend ⇒ primaire club
    [InlineData("lijst", "AB CD", null)]            // ongeldige vorm ⇒ primaire club
    [InlineData("fout", "ONBEKEND", "ONBEKEND")]    // NietBeschikbaar ⇒ geldige waarde zoals vóór #1677
    [InlineData("fout", "AB CD", null)]             // ongeldige vorm ⇒ primaire club, ook zonder lijst
    [InlineData("fout", null, null)]
    public void CodeVoorVerzoek_PerToestandEnVorm(string bron, string? header, string? verwacht)
    {
        var clubs = bron == "lijst" ? new BekendeClubs(() => Lijst) : NietBeschikbaar();

        clubs.Zoek(header).CodeVoorVerzoek.Should().Be(verwacht);
    }

    [Fact]
    public void DatabaseFout_ZonderEerdereLading_IsNietBeschikbaar_EnValtNietTerugOpPrimair()
    {
        var resultaat = NietBeschikbaar().Zoek("ALLSTARS");

        resultaat.Should().Be(ClubZoekResultaat.NietBeschikbaar("ALLSTARS"));
        resultaat.CodeVoorVerzoek.Should().Be("ALLSTARS");
    }

    [Fact]
    public void DatabaseFout_NaEerdereLading_BehoudtLaatsteLijst()
    {
        var nu = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var faal = false;
        var clubs = new BekendeClubs(() => faal ? throw new InvalidOperationException() : Lijst,
            TimeSpan.FromSeconds(10), () => nu);
        clubs.Zoek("ALLSTARS").Should().Be(Allstars);

        faal = true;
        nu = nu.AddSeconds(11);

        clubs.Zoek("ALLSTARS").Should().Be(Allstars);
        clubs.WachtOpVerversing(Wacht).Should().BeTrue();
        clubs.Zoek("ALLSTARS").Should().Be(Allstars);
        clubs.Zoek("ONBEKEND").Should().Be(Onbekend);
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
    public void MislukteEersteLading_ProbeertPasNaFoutTerugvalOpnieuw_EnBlijftTotDanNietBeschikbaar()
    {
        var nu = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var aanroepen = 0;
        var faal = true;
        var clubs = new BekendeClubs(() => { aanroepen++; return faal ? throw new InvalidOperationException() : Lijst; },
            TimeSpan.FromSeconds(60), () => nu);

        clubs.Zoek("A").Status.Should().Be(ClubZoekStatus.NietBeschikbaar);
        clubs.Zoek("B").Status.Should().Be(ClubZoekStatus.NietBeschikbaar);
        aanroepen.Should().Be(1);

        faal = false;
        nu = nu + BekendeClubs.FoutTerugval;
        clubs.Zoek("ALLSTARS").Should().Be(Allstars);
        aanroepen.Should().Be(2);
    }

    // De lezers mogen hier langer wachten dan de verversing hangt: een lezer die bij een verlopen
    // lijst ten onrechte op de verversing wacht, krijgt dan de nieuwe lijst (OUD onbekend) in plaats
    // van het stale antwoord. Zo hangt de test niet af van timing; de eerste lading is gewoon klaar (#1696).
    [Fact]
    public void VerlopenLijst_GeeftStaleDirect_EnVerverstOpDeAchtergrond()
    {
        using var v = new HangendeVerversing();
        var clubs = v.Maak(eersteLadingTimeout: Wacht * 3);
        clubs.Zoek("OUD").Should().Be(ClubZoekResultaat.Bekend("OUD"));

        v.Nu = v.Nu.AddSeconds(11);
        clubs.Zoek("OUD").Should().Be(ClubZoekResultaat.Bekend("OUD"));   // stale, zonder op de verversing te wachten
        v.Gestart.Wait(Wacht).Should().BeTrue();
        clubs.Zoek("OUD").Should().Be(ClubZoekResultaat.Bekend("OUD"));   // de verversing hangt nog: oude lijst
        v.Aanroepen.Should().Be(2);                       // geen tweede verversing tijdens de eerste

        v.Vrij.Set();
        clubs.WachtOpVerversing(Wacht).Should().BeTrue();
        clubs.Zoek("NIEUW").Should().Be(ClubZoekResultaat.Bekend("NIEUW"));
    }

    [Fact]
    public void OnbekendeWaarde_TijdensHangendeVerversing_GeeftNaBegrensdeWachtOnbekend()
    {
        using var v = new HangendeVerversing();
        var clubs = v.Maak(eersteLadingTimeout: TimeSpan.FromMilliseconds(50));
        clubs.Zoek("OUD");                                // start de eerste lading; de korte wacht kan verlopen
        clubs.WachtOpVerversing(Wacht).Should().BeTrue();
        clubs.Zoek("OUD").Should().Be(ClubZoekResultaat.Bekend("OUD"));

        v.Nu = v.Nu.AddSeconds(11);
        clubs.Zoek("OUD");                                // start de (hangende) verversing
        v.Gestart.Wait(Wacht).Should().BeTrue();
        clubs.Zoek("NIEUW").Should().Be(Onbekend);        // begrensde wacht, de lading hangt nog
        v.Aanroepen.Should().Be(2);

        v.Vrij.Set();
        clubs.WachtOpVerversing(Wacht).Should().BeTrue();
        clubs.Zoek("NIEUW").Should().Be(ClubZoekResultaat.Bekend("NIEUW"));
    }

    /// <summary>Eerste lading geeft direct OUD; elke volgende hangt tot <see cref="Vrij"/> en geeft NIEUW.</summary>
    private sealed class HangendeVerversing : IDisposable
    {
        public ManualResetEventSlim Gestart { get; } = new();
        public ManualResetEventSlim Vrij { get; } = new();
        public DateTime Nu { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public int Aanroepen => Volatile.Read(ref _aanroepen);
        private int _aanroepen;

        public BekendeClubs Maak(TimeSpan eersteLadingTimeout) => new(() =>
        {
            if (Interlocked.Increment(ref _aanroepen) == 1) return ["OUD"];
            Gestart.Set();
            Vrij.Wait(Wacht);
            return ["NIEUW"];
        }, TimeSpan.FromSeconds(10), () => Nu, eersteLadingTimeout);

        // Laat een eventueel nog hangende lading los; de events zelf blijven voor de GC (een
        // achtergrondlading kan er nog op wachten).
        public void Dispose() => Vrij.Set();
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
        clubs.Zoek("ALLSTARS").Should().Be(Allstars);
        aanroepen.Should().Be(2);
    }

    [Fact]
    public void EersteLadingHangt_GeeftNaTimeoutNietBeschikbaar_ZonderTeBlijvenWachten()
    {
        using var vrij = new ManualResetEventSlim();
        var clubs = new BekendeClubs(() => { vrij.Wait(Wacht); return Lijst; },
            eersteLadingTimeout: TimeSpan.FromMilliseconds(50));

        clubs.Zoek("ALLSTARS").Should().Be(ClubZoekResultaat.NietBeschikbaar("ALLSTARS"));

        vrij.Set();
        clubs.WachtOpVerversing(Wacht).Should().BeTrue();
        clubs.Zoek("ALLSTARS").Should().Be(Allstars);
    }

    [Fact]
    public async Task GelijktijdigeEersteLezers_WachtenOpDezelfdeLading_EnKrijgenHetzelfdeResultaat()
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
        var tweede = Task.Run(() => clubs.Zoek("allstars"));

        (await Task.WhenAny(tweede, Task.Delay(200))).Should().NotBeSameAs(tweede);   // wacht op de lopende lading
        vrij.Set();
        var resultaten = await Task.WhenAll(lader, tweede).WaitAsync(Wacht);
        resultaten.Should().Equal(Allstars, Allstars);
        aanroepen.Should().Be(1);
    }
}
