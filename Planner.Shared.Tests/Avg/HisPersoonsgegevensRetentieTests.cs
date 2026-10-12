using AwesomeAssertions;
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

    [Fact]
    public async Task VoerUitAsync_ZonderAfgerondSeizoen_RoeptDeWisstapNietAan()
    {
        var aangeroepen = false;
        var resultaat = await HisPersoonsgegevensRetentie.VoerUitAsync(
            () => Task.FromResult<IReadOnlyList<DateOnly>>([new DateOnly(2027, 6, 30)]),
            _ => { aangeroepen = true; return Task.FromResult((1, 1)); },
            NullLogger.Instance, Vandaag);

        aangeroepen.Should().BeFalse();
        resultaat.Should().Be(new HisRetentieResultaat(null, 0, 0));
    }

    [Fact]
    public async Task VoerUitAsync_GeeftDeGrensDoorEnTeltDeGewisteRijen()
    {
        DateOnly? doorgegeven = null;
        var resultaat = await HisPersoonsgegevensRetentie.VoerUitAsync(
            () => Task.FromResult<IReadOnlyList<DateOnly>>([new DateOnly(2026, 6, 30), new DateOnly(2027, 6, 30)]),
            grens => { doorgegeven = grens; return Task.FromResult((3, 2)); },
            NullLogger.Instance, Vandaag);

        doorgegeven.Should().Be(new DateOnly(2026, 6, 30));
        resultaat.Should().Be(new HisRetentieResultaat(new DateOnly(2026, 6, 30), 3, 2));
    }
    [Fact]
    public async Task VoerTimerUitAsync_WachtOpDeDatabase_GebruiktEenVerbinding_EnSluitHem()
    {
        var volgorde = new List<string>();
        var verbinding = new NepVerbinding();
        await HisPersoonsgegevensRetentie.VoerTimerUitAsync(
            () => { volgorde.Add("wacht"); return Task.CompletedTask; },
            () => { volgorde.Add("open"); return Task.FromResult(verbinding); },
            v => { v.Should().BeSameAs(verbinding); return Task.FromResult<IReadOnlyList<DateOnly>>([new DateOnly(2000, 6, 30)]); },
            (v, _) => { v.Should().BeSameAs(verbinding); volgorde.Add("wis"); return Task.FromResult((0, 0)); },
            NullLogger.Instance);

        volgorde.Should().Equal("wacht", "open", "wis");
        verbinding.Gesloten.Should().BeTrue();
    }

    [Fact]
    public async Task VoerTimerUitAsync_FoutWordtDoorgegooid_EnDeVerbindingGesloten()
    {
        var verbinding = new NepVerbinding();
        var act = () => HisPersoonsgegevensRetentie.VoerTimerUitAsync(
            () => Task.CompletedTask,
            () => Task.FromResult(verbinding),
            _ => throw new InvalidOperationException("seizoenstabel onbereikbaar"),
            (_, _) => Task.FromResult((0, 0)),
            NullLogger.Instance);

        await act.Should().ThrowAsync<InvalidOperationException>("de Functions-host moet een mislukte run zien");
        verbinding.Gesloten.Should().BeTrue();
    }

    private sealed class NepVerbinding : IAsyncDisposable
    {
        public bool Gesloten { get; private set; }
        public ValueTask DisposeAsync() { Gesloten = true; return ValueTask.CompletedTask; }
    }
}
