using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

public class PlanningVoorlaadCacheTests
{
    private static ApiResult<VeldbezettingAntwoordDto> Goed() => ApiResult<VeldbezettingAntwoordDto>.Ok(new()
    {
        Sportlink = new() { Geraadpleegd = true, GeraadpleegdOpUtc = DateTime.UtcNow, Reden = "Ok" }
    });

    [Fact]
    public async Task LaadtVierZaterdagenSerieel_EnToontKopieZonderOpNieuweAanroepTeWachten()
    {
        var opvragen = new List<string>();
        using var cache = new PlanningVoorlaadCache((datum, _) => { opvragen.Add(datum); return Task.FromResult(Goed()); });
        await cache.LaadVolgendeZaterdagenAsync(new(2026, 11, 16));
        opvragen.Should().Equal("2026-11-21", "2026-11-28", "2026-12-05", "2026-12-12");
        cache.Lees("2026-11-28").Should().NotBeNull();
        await cache.LaadVolgendeZaterdagenAsync(new(2026, 11, 16));
        opvragen.Should().HaveCount(4);
    }

    [Fact]
    public async Task Voorgrond_DeeltLopendeAchtergrondaanvraag()
    {
        var wacht = new TaskCompletionSource<ApiResult<VeldbezettingAntwoordDto>>();
        int calls = 0;
        using var cache = new PlanningVoorlaadCache((_, _) => { calls++; return wacht.Task; });
        var eerste = cache.LaadAsync("2026-11-21");
        var tweede = cache.LaadAsync("2026-11-21");
        tweede.Should().BeSameAs(eerste);
        wacht.SetResult(Goed());
        await Task.WhenAll(eerste, tweede);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ClubwisselEnTerug_NegeertLaatAntwoord_EnMutatieWistKopie()
    {
        var wacht = new TaskCompletionSource<ApiResult<VeldbezettingAntwoordDto>>();
        var begonnen = new TaskCompletionSource();
        using var cache = new PlanningVoorlaadCache((_, _) => { begonnen.TrySetResult(); return wacht.Task; });
        cache.KiesClub("CLUB-A");
        var oud = cache.LaadAsync("2026-11-21");
        await begonnen.Task;
        cache.KiesClub("CLUB-B");
        cache.KiesClub("CLUB-A");
        wacht.SetResult(Goed());
        await oud;
        cache.Lees("2026-11-21").Should().BeNull();
        await cache.LaadAsync("2026-11-21");
        cache.Lees("2026-11-21").Should().NotBeNull();
        cache.Wis();
        cache.Lees("2026-11-21").Should().BeNull();
    }

    [Fact]
    public async Task VerlopenKopieEnFoutOfTerugval_WordenNietHergebruikt()
    {
        var nu = DateTime.UtcNow;
        var antwoord = Goed();
        using var cache = new PlanningVoorlaadCache((_, _) => Task.FromResult(antwoord), () => nu);
        await cache.LaadAsync("2026-11-21");
        nu += PlanningVoorlaadCache.Geldigheid;
        cache.Lees("2026-11-21").Should().BeNull();
        antwoord = ApiResult<VeldbezettingAntwoordDto>.Fail("niet beschikbaar");
        await cache.LaadAsync("2026-11-21");
        cache.Lees("2026-11-21").Should().BeNull();
        antwoord = ApiResult<VeldbezettingAntwoordDto>.Ok(new() { Sportlink = new() { Geraadpleegd = false } });
        await cache.LaadAsync("2026-11-21");
        cache.Lees("2026-11-21").Should().BeNull();
    }

    [Fact]
    public async Task Navigatie_AnnuleertAchtergrondEnBegintGeenVolgendeDag()
    {
        var begonnen = new TaskCompletionSource();
        int calls = 0;
        using var cache = new PlanningVoorlaadCache(async (_, ct) =>
        {
            calls++; begonnen.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Goed();
        });
        var taak = cache.LaadVolgendeZaterdagenAsync(new(2026, 11, 16));
        await begonnen.Task;
        cache.Dispose();
        await taak;
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Datumwissel_StoptRijNaHuidigeAanvraag_EnFoutStoptVooruitladen()
    {
        var begonnen = new TaskCompletionSource();
        var wacht = new TaskCompletionSource<ApiResult<VeldbezettingAntwoordDto>>();
        int calls = 0;
        using var cache = new PlanningVoorlaadCache((_, _) => { calls++; begonnen.TrySetResult(); return wacht.Task; });
        var taak = cache.LaadVolgendeZaterdagenAsync(new(2026, 11, 16));
        await begonnen.Task;
        cache.StopVoorladen();
        wacht.SetResult(Goed());
        await taak;
        calls.Should().Be(1);
        using var fout = new PlanningVoorlaadCache((_, _) => { calls++; return Task.FromResult(ApiResult<VeldbezettingAntwoordDto>.Fail("fout")); });
        await fout.LaadVolgendeZaterdagenAsync(new(2026, 11, 16));
        calls.Should().Be(2);
    }
}
