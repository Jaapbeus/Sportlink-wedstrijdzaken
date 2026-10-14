using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Avg;
using Xunit;

namespace Planner.Shared.Tests.Avg;

/// <summary>
/// Seizoensretentie voor de persoonskolommen in de historietabellen (#1698). De regel: bewaren tot
/// het einde van het seizoen waarin de wedstrijd valt, daarna leegmaken. De grens is dus het
/// laatste seizoenseinde dat vóór vandaag ligt; wedstrijden tot en met die datum zijn aan de beurt.
/// </summary>
public class HisPersoonsgegevensRetentieTests
{
    private static readonly DateOnly Vandaag = new(2026, 10, 12);

    [Fact]
    public void BepaalGrens_ZonderSeizoenen_IsNull_DusNietsOpruimen()
        => HisPersoonsgegevensRetentie.BepaalGrens([], Vandaag).Should().BeNull();

    [Fact]
    public void BepaalGrens_AlleenLopendSeizoen_IsNull()
        => HisPersoonsgegevensRetentie.BepaalGrens([new DateOnly(2027, 6, 30)], Vandaag).Should().BeNull();

    [Fact]
    public void BepaalGrens_KiestHetLaatsteAfgerondeSeizoenseinde()
        => HisPersoonsgegevensRetentie.BepaalGrens(
                [new DateOnly(2025, 6, 30), new DateOnly(2026, 6, 30), new DateOnly(2027, 6, 30)], Vandaag)
            .Should().Be(new DateOnly(2026, 6, 30));

    [Fact]
    public void BepaalGrens_VolgordeVanDeInvoerMaaktNietUit()
        => HisPersoonsgegevensRetentie.BepaalGrens(
                [new DateOnly(2027, 6, 30), new DateOnly(2026, 6, 30), new DateOnly(2025, 6, 30)], Vandaag)
            .Should().Be(new DateOnly(2026, 6, 30));

    [Fact]
    public void BepaalGrens_SeizoenDatVandaagEindigt_IsNogNietAfgerond()
        => HisPersoonsgegevensRetentie.BepaalGrens([new DateOnly(2026, 10, 12)], Vandaag).Should().BeNull(
            "de laatste speeldag van een seizoen hoort nog bij het seizoen; pas de dag erna mag er gewist worden");

    [Fact]
    public void BepaalGrens_SeizoenDatGisterenEindigde_IsAfgerond()
        => HisPersoonsgegevensRetentie.BepaalGrens([new DateOnly(2026, 10, 11)], Vandaag)
            .Should().Be(new DateOnly(2026, 10, 11));

    [Fact]
    public void Kolommen_ZijnLowercase_UniekEnNietLeeg()
    {
        foreach (var kolommen in new[] { HisPersoonsgegevensRetentie.MatchesKolommen, HisPersoonsgegevensRetentie.MatchDetailsKolommen })
        {
            kolommen.Should().NotBeEmpty();
            kolommen.Should().OnlyHaveUniqueItems();
            kolommen.Should().AllSatisfy(k => k.Should().Be(k.ToLowerInvariant()));
        }
    }

    [Fact]
    public void Kolommen_BevattenGeenSleutelOfDatumKolom()
    {
        // Een sleutel- of datumkolom leegmaken zou de rij onvindbaar maken of de retentie zelf breken.
        var verboden = new[] { "wedstrijdcode", "interncode", "wedstrijddatum", "matchdate", "matchdatetime", "clubcode" };
        HisPersoonsgegevensRetentie.MatchesKolommen.Should().NotIntersectWith(verboden);
        HisPersoonsgegevensRetentie.MatchDetailsKolommen.Should().NotIntersectWith(verboden);
    }

    private static IReadOnlyList<ClubSeizoenEinde> Einden(params (string Club, DateOnly Einde)[] rijen)
        => rijen.Select(r => new ClubSeizoenEinde(r.Club, r.Einde)).ToList();

    [Fact]
    public async Task VoerUitAsync_ZonderAfgerondSeizoen_RoeptDeWisstapNietAan()
    {
        var aangeroepen = false;
        var resultaat = await HisPersoonsgegevensRetentie.VoerUitAsync(
            () => Task.FromResult(Einden(("ALLSTARS", new DateOnly(2027, 6, 30)))),
            (_, _) => { aangeroepen = true; return Task.FromResult((1, 1)); },
            NullLogger.Instance, Vandaag);

        aangeroepen.Should().BeFalse();
        resultaat.Should().Be(new HisRetentieResultaat(null, 0, 0));
    }

    [Fact]
    public async Task VoerUitAsync_GeeftDeGrensDoorEnTeltDeGewisteRijen()
    {
        DateOnly? doorgegeven = null;
        var resultaat = await HisPersoonsgegevensRetentie.VoerUitAsync(
            () => Task.FromResult(Einden(("ALLSTARS", new DateOnly(2026, 6, 30)), ("ALLSTARS", new DateOnly(2027, 6, 30)))),
            (_, grens) => { doorgegeven = grens; return Task.FromResult((3, 2)); },
            NullLogger.Instance, Vandaag);

        doorgegeven.Should().Be(new DateOnly(2026, 6, 30));
        resultaat.Should().Be(new HisRetentieResultaat(new DateOnly(2026, 6, 30), 3, 2));
    }

    [Fact]
    public async Task VoerUitAsync_TweeClubsMetVerschillendeStartmaand_KrijgenElkEenEigenGrens()
    {
        // #1715: club A start in juli (seizoen 2025-2026 eindigt 30 juni 2026), club B in september
        // (eindigt 31 augustus 2026). Op 15 juli 2026 is dat van A afgerond en dat van B nog niet: de
        // retentie mag voor B dus nog niets wissen en niet de grens van A overnemen.
        var vandaag = new DateOnly(2026, 7, 15);
        var gewist = new List<(string Club, DateOnly Grens)>();

        var resultaat = await HisPersoonsgegevensRetentie.VoerUitAsync(
            () => Task.FromResult(Einden(
                ("CLUBA", new DateOnly(2025, 6, 30)), ("CLUBA", new DateOnly(2026, 6, 30)),
                ("CLUBB", new DateOnly(2025, 8, 31)), ("CLUBB", new DateOnly(2026, 8, 31)))),
            (club, grens) => { gewist.Add((club, grens)); return Task.FromResult((1, 1)); },
            NullLogger.Instance, vandaag);

        gewist.Should().Equal(("CLUBA", new DateOnly(2026, 6, 30)), ("CLUBB", new DateOnly(2025, 8, 31)));
        resultaat.Should().Be(new HisRetentieResultaat(new DateOnly(2026, 6, 30), 2, 2));
    }

    [Fact]
    public async Task VoerUitAsync_ClubZonderAfgerondSeizoen_BlijftBuitenDeWisstap()
    {
        var gewist = new List<string>();

        await HisPersoonsgegevensRetentie.VoerUitAsync(
            () => Task.FromResult(Einden(("CLUBA", new DateOnly(2026, 6, 30)), ("CLUBB", new DateOnly(2027, 6, 30)))),
            (club, _) => { gewist.Add(club); return Task.FromResult((0, 0)); },
            NullLogger.Instance, Vandaag);

        gewist.Should().Equal("CLUBA");
    }

    [Fact]
    public async Task VoerStapUitAsync_GebruiktDeOpslagVanDeTier_EnGeeftHetResultaatTerug()
    {
        var opslag = new NepOpslag(Einden(("ALLSTARS", new DateOnly(2026, 6, 30))), (4, 5));

        var resultaat = await HisPersoonsgegevensRetentie.VoerStapUitAsync(opslag, NullLogger.Instance, Vandaag);

        opslag.GewistTotEnMet.Should().Be(new DateOnly(2026, 6, 30));
        opslag.GewistVoorClub.Should().Be("ALLSTARS");
        resultaat.Should().Be(new HisRetentieResultaat(new DateOnly(2026, 6, 30), 4, 5));
    }

    [Fact]
    public async Task VoerStapUitAsync_ZonderDatum_RekentMetVandaagInUtc()
    {
        // Een seizoen dat gisteren (UTC) eindigde, is afgerond; een dat morgen eindigt nog niet.
        // Zonder expliciete datum moet de stap dus vandaag als peildatum nemen, niet een vaste,
        // minimale of maximale datum. Ook rond middernacht UTC blijft de verwachting gelijk.
        var vandaagUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        var gisteren = vandaagUtc.AddDays(-1);
        var opslag = new NepOpslag(Einden(("ALLSTARS", gisteren), ("ALLSTARS", vandaagUtc.AddDays(1))), (1, 0));

        var resultaat = await HisPersoonsgegevensRetentie.VoerStapUitAsync(opslag, NullLogger.Instance);

        resultaat.Grens.Should().Be(gisteren);
    }

    [Fact]
    public async Task VoerStapUitAsync_FoutWordtGelogdEnDoorgegooid()
    {
        var log = new TelLogger();
        var act = () => HisPersoonsgegevensRetentie.VoerStapUitAsync(
            new NepOpslag(null, (0, 0)), log, Vandaag);

        await act.Should().ThrowAsync<InvalidOperationException>("de Functions-host moet een mislukte run zien");
        log.Fouten.Should().Be(1);
    }

    private sealed class NepOpslag(IReadOnlyList<ClubSeizoenEinde>? seizoenseinden, (int, int) gewist) : IHisRetentieOpslag
    {
        public DateOnly? GewistTotEnMet { get; private set; }
        public string? GewistVoorClub { get; private set; }

        public Task<IReadOnlyList<ClubSeizoenEinde>> LeesSeizoenseindenAsync()
            => seizoenseinden is null
                ? throw new InvalidOperationException("seizoenstabel onbereikbaar")
                : Task.FromResult(seizoenseinden);

        public Task<(int Matches, int MatchDetails)> WisTotEnMetAsync(string clubCode, DateOnly grens)
        {
            GewistVoorClub = clubCode;
            GewistTotEnMet = grens;
            return Task.FromResult(gewist);
        }
    }

    private sealed class TelLogger : ILogger
    {
        public int Fouten { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Fouten++;
        }
    }
}
