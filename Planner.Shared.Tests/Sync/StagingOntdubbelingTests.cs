using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

/// <summary>
/// De gedeelde ontdubbeling vóór staging (#1712): per sleutel wint de laatste vermelding, zodat de
/// merge naar his nooit twee keer dezelfde rij raakt (Postgres 21000, SQL Server 8672).
/// </summary>
public class StagingOntdubbelingTests
{
    private sealed record Rij(int Sleutel, string Waarde);

    [Fact]
    public void LaatsteWint_DubbeleSleutel_HoudtDeLaatsteVermelding()
    {
        var rijen = new[] { new Rij(1, "eerste"), new Rij(2, "ander"), new Rij(1, "laatste") };

        var uniek = StagingOntdubbeling.LaatsteWint(rijen, r => r.Sleutel);

        uniek.Should().Equal(new Rij(2, "ander"), new Rij(1, "laatste"));
    }

    [Fact]
    public void LaatsteWint_ZonderDubbeleSleutels_GeeftDezelfdeLijstTerug()
    {
        var rijen = new[] { new Rij(1, "a"), new Rij(2, "b") };

        StagingOntdubbeling.LaatsteWint(rijen, r => r.Sleutel).Should().BeSameAs(rijen);
    }

    [Fact]
    public void LaatsteWint_LegeLijst_BlijftLeeg()
        => StagingOntdubbeling.LaatsteWint(Array.Empty<Rij>(), r => r.Sleutel).Should().BeEmpty();

    [Fact]
    public void LaatsteWint_DrieKeerDezelfdeSleutel_HoudtEen()
    {
        var rijen = new[] { new Rij(5, "1"), new Rij(5, "2"), new Rij(5, "3") };

        StagingOntdubbeling.LaatsteWint(rijen, r => r.Sleutel).Should().Equal(new Rij(5, "3"));
    }

    [Fact]
    public void TeamSleutel_OntbrekendePoulecode_IsGelijkAanElkaarMaarNietAanNul()
    {
        StagingOntdubbeling.TeamSleutel(10, 1, null).Should().Be(StagingOntdubbeling.TeamSleutel(10, 1, null));
        StagingOntdubbeling.TeamSleutel(10, 1, null).Should().NotBe(StagingOntdubbeling.TeamSleutel(10, 1, 0));
    }

    [Fact]
    public void TeamSleutel_VerschilInElkDeel_IsEenAndereSleutel()
    {
        var basis = StagingOntdubbeling.TeamSleutel(10, 1, 7);

        basis.Should().NotBe(StagingOntdubbeling.TeamSleutel(11, 1, 7));
        basis.Should().NotBe(StagingOntdubbeling.TeamSleutel(10, 2, 7));
        basis.Should().NotBe(StagingOntdubbeling.TeamSleutel(10, 1, 8));
    }

    [Fact]
    public void LaatsteWint_MetLogger_MeldtAlleenHetAantal()
    {
        var log = new TellendeLogger();
        var rijen = new[] { new Rij(1, "geheim-a"), new Rij(1, "geheim-b"), new Rij(2, "c") };

        var uniek = StagingOntdubbeling.LaatsteWint(rijen, r => r.Sleutel, log, "TEAMS");

        uniek.Should().HaveCount(2);
        log.Meldingen.Should().ContainSingle()
            .Which.Should().Be("TEAMS - 1 dubbele vermelding(en) met dezelfde sleutel; de laatste wint.");
    }

    [Fact]
    public void LaatsteWint_MetLogger_ZonderDubbelen_MeldtNiets()
    {
        var log = new TellendeLogger();

        StagingOntdubbeling.LaatsteWint(new[] { new Rij(1, "a") }, r => r.Sleutel, log, "TEAMS");

        log.Meldingen.Should().BeEmpty();
    }

    private sealed class TellendeLogger : ILogger
    {
        public List<string> Meldingen { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Meldingen.Add(formatter(state, exception));
    }
}
