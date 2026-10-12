using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

/// <summary>
/// Restpunten van de clublijst (#1689): begrensd herladen bij een onbekende waarde, deterministische
/// keuze bij hoofdletter-varianten en een geaggregeerde log zonder waarden bij structureel falen.
/// </summary>
public class BekendeClubsHerladenEnLogTests
{
    private static readonly TimeSpan Wacht = TimeSpan.FromSeconds(10);
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OnbekendeWaarde_NaOndergrens_HerlaadtEnHerkentNieuweClubMeteen()
    {
        var nu = Start;
        var lijst = new List<string> { "ALLSTARS" };
        var aanroepen = 0;
        var clubs = new BekendeClubs(() => { aanroepen++; return lijst.ToArray(); }, TimeSpan.FromSeconds(60), () => nu);
        clubs.Zoek("ALLSTARS").Should().Be(ClubZoekResultaat.Bekend("ALLSTARS"));

        lijst.Add("NIEUW");
        nu = Start + BekendeClubs.HerlaadOndergrens;

        clubs.Zoek("NIEUW").Should().Be(ClubZoekResultaat.Bekend("NIEUW"));
        aanroepen.Should().Be(2);
    }

    [Fact]
    public void OnbekendeWaarden_HerladenHoogstensEensPerOndergrens()
    {
        var nu = Start;
        var aanroepen = 0;
        var clubs = new BekendeClubs(() => { Interlocked.Increment(ref aanroepen); return ["ALLSTARS"]; },
            TimeSpan.FromSeconds(60), () => nu);
        clubs.Zoek("ALLSTARS");

        nu = Start + BekendeClubs.HerlaadOndergrens - TimeSpan.FromSeconds(1);
        for (var i = 0; i < 5; i++) clubs.Zoek($"ONBEKEND{i}").Should().Be(ClubZoekResultaat.Onbekend);
        aanroepen.Should().Be(1);

        nu = Start + BekendeClubs.HerlaadOndergrens;
        for (var i = 0; i < 5; i++) clubs.Zoek($"ONBEKEND{i}").Should().Be(ClubZoekResultaat.Onbekend);
        clubs.WachtOpVerversing(Wacht).Should().BeTrue();
        aanroepen.Should().Be(2);
    }

    [Fact]
    public void OnbekendeWaarde_NaMislukteLading_VolgtDeFoutTerugval()
    {
        var nu = Start;
        var aanroepen = 0;
        var clubs = new BekendeClubs(() => ++aanroepen == 1 ? ["ALLSTARS"] : throw new InvalidOperationException(),
            TimeSpan.FromSeconds(10), () => nu);
        clubs.Zoek("ALLSTARS");
        nu = Start + TimeSpan.FromSeconds(10);
        clubs.Zoek("ALLSTARS");                       // verlopen: verversing faalt op de achtergrond
        clubs.WachtOpVerversing(Wacht).Should().BeTrue();
        aanroepen.Should().Be(2);

        nu += BekendeClubs.HerlaadOndergrens;         // ondergrens voorbij, foutterugval nog niet
        clubs.Zoek("ONBEKEND").Should().Be(ClubZoekResultaat.Onbekend);
        aanroepen.Should().Be(2);
    }

    [Fact]
    public void BekendeWaarde_HerlaadtNietBinnenGeldigheid()
    {
        var nu = Start;
        var aanroepen = 0;
        var clubs = new BekendeClubs(() => { aanroepen++; return ["ALLSTARS"]; }, TimeSpan.FromSeconds(60), () => nu);
        clubs.Zoek("ALLSTARS");

        nu = Start + TimeSpan.FromSeconds(30);
        clubs.Zoek("allstars").Should().Be(ClubZoekResultaat.Bekend("ALLSTARS"));
        aanroepen.Should().Be(1);
    }

    [Theory]
    [InlineData("ABC", "ABC")]
    [InlineData("abc", "abc")]
    [InlineData("Abc", "Abc")]
    [InlineData("aBC", "ABC")]   // geen exacte match: vaste keuze (ordinaal kleinste), los van de rijvolgorde
    public void HoofdletterVarianten_ExacteMatchEerst_DaarnaDeterministisch(string gevraagd, string verwacht)
    {
        foreach (var volgorde in new[] { new[] { "abc", "Abc", "ABC" }, new[] { "ABC", "Abc", "abc" }, new[] { "Abc", "abc", "ABC" } })
            new BekendeClubs(() => volgorde).Zoek(gevraagd).Should().Be(ClubZoekResultaat.Bekend(verwacht));
    }

    [Fact]
    public void StructureelFalen_LogtGeaggregeerdEenmaal_ZonderWaarden_EnMeldtHerstel()
    {
        var nu = Start;
        var faal = true;
        var log = new LogOpvang();
        var clubs = new BekendeClubs(() => faal ? throw new InvalidOperationException("verbinding-geheim-host") : ["ALLSTARS"],
            TimeSpan.FromSeconds(60), () => nu) { Log = log };

        void Poging()
        {
            clubs.Zoek("ALLSTARS");
            clubs.WachtOpVerversing(Wacht).Should().BeTrue();
            nu += BekendeClubs.FoutTerugval;
        }

        for (var i = 1; i < BekendeClubs.StructureleFoutDrempel; i++) Poging();
        log.Regels.Should().BeEmpty();

        Poging();
        log.Regels.Should().ContainSingle();
        var (niveau, tekst, fout) = log.Regels[0];
        niveau.Should().Be(LogLevel.Warning);
        tekst.Should().Contain(BekendeClubs.StructureleFoutDrempel.ToString()).And.Contain(nameof(InvalidOperationException));
        tekst.Should().NotContain("geheim").And.NotContain("ALLSTARS");
        fout.Should().BeNull();   // geen exception-object: de melding kan verbindingsgegevens bevatten

        Poging();
        Poging();
        log.Regels.Should().ContainSingle();   // geaggregeerd, niet per poging

        faal = false;
        Poging();
        log.Regels.Should().HaveCount(2);
        log.Regels[1].Niveau.Should().Be(LogLevel.Information);
        log.Regels[1].Tekst.Should().NotContain("ALLSTARS");
        clubs.Zoek("ALLSTARS").Should().Be(ClubZoekResultaat.Bekend("ALLSTARS"));
    }

    [Fact]
    public void AfwisselendFalen_IsNietStructureel_EnLogtNiets()
    {
        var nu = Start;
        var aanroepen = 0;
        var log = new LogOpvang();
        var clubs = new BekendeClubs(() => ++aanroepen % 2 == 0 ? throw new InvalidOperationException() : ["ALLSTARS"],
            TimeSpan.FromSeconds(10), () => nu) { Log = log };

        for (var i = 0; i < 2 * BekendeClubs.StructureleFoutDrempel + 1; i++)
        {
            clubs.Zoek("ALLSTARS");
            clubs.WachtOpVerversing(Wacht).Should().BeTrue();
            nu += TimeSpan.FromSeconds(20);
        }

        aanroepen.Should().Be(2 * BekendeClubs.StructureleFoutDrempel + 1);
        log.Regels.Should().BeEmpty();
    }

    private sealed class LogOpvang : ILogger
    {
        private readonly object _slot = new();
        public List<(LogLevel Niveau, string Tekst, Exception? Fout)> Regels { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_slot) Regels.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
