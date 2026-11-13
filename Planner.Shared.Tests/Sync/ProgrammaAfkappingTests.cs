using AwesomeAssertions;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

/// <summary>
/// #1915: Sportlinks <c>/programma</c> geeft per aanroep maximaal 100 rijen. Een venster dat dat plafond raakt wordt
/// opnieuw opgehaald, gesplitst in thuis en uit; alleen als ook dat nog afgekapt is geldt de ophaling als onvolledig.
/// </summary>
public class ProgrammaAfkappingTests
{
    private const string Url = "https://dataservice.invalid/programma?clientId=x&weekoffset=0";

    private static IReadOnlyList<int> Reeks(int vanaf, int aantal) => Enumerable.Range(vanaf, aantal).ToList();

    /// <summary>Een nagebootste dataservice: het antwoord hangt af van het thuis/uit-filter in de URL.</summary>
    private static Func<string, Task<IReadOnlyList<int>>> Dataservice(
        IReadOnlyList<int> alles, IReadOnlyList<int> thuis, IReadOnlyList<int> uit, List<string>? aanroepen = null)
        => url =>
        {
            aanroepen?.Add(url);
            if (url.Contains("thuis=JA&uit=NEE")) return Task.FromResult(thuis);
            if (url.Contains("thuis=NEE&uit=JA")) return Task.FromResult(uit);
            return Task.FromResult((IReadOnlyList<int>)alles.Take(ProgrammaAfkapping.Plafond).ToList());
        };

    [Fact]
    public async Task HaalVolledig_ONDER_HetPlafond_IsEenAanroepEnVolledig()
    {
        var aanroepen = new List<string>();

        var r = await ProgrammaAfkapping.HaalVolledigAsync(Url, Dataservice(Reeks(1, 99), [], [], aanroepen));

        r.Rijen.Should().HaveCount(99);
        r.Volledig.Should().BeTrue();
        r.Gesplitst.Should().BeFalse();
        aanroepen.Should().ContainSingle().Which.Should().Be(Url);
    }

    [Fact]
    public async Task HaalVolledig_OpHetPlafond_SplitstInThuisEnUitEnIsVolledig()
    {
        // 110 wedstrijden in het venster: de gewone aanroep toont er 100, thuis (51) + uit (59) samen alle 110.
        var r = await ProgrammaAfkapping.HaalVolledigAsync(Url, Dataservice(Reeks(1, 110), Reeks(1, 51), Reeks(52, 59)));

        r.Rijen.Should().HaveCount(110);
        r.Volledig.Should().BeTrue();
        r.Gesplitst.Should().BeTrue();
    }

    [Fact]
    public async Task HaalVolledig_Splitsing_BehoudtDeOorspronkelijkeQueryEnVoegtHetFilterToe()
    {
        var aanroepen = new List<string>();

        await ProgrammaAfkapping.HaalVolledigAsync(Url, Dataservice(Reeks(1, 110), Reeks(1, 51), Reeks(52, 59), aanroepen));

        aanroepen.Should().Equal(Url, Url + "&thuis=JA&uit=NEE", Url + "&thuis=NEE&uit=JA");
    }

    [Fact]
    public async Task HaalVolledig_DeelvensterOokOpHetPlafond_IsOnvolledig()
    {
        var r = await ProgrammaAfkapping.HaalVolledigAsync(Url, Dataservice(Reeks(1, 300), Reeks(1, 100), Reeks(101, 60)));

        r.Volledig.Should().BeFalse("ook de thuis-verzameling is afgekapt: reconciliatie mag hier niet op vertrouwen");
        r.Gesplitst.Should().BeTrue();
        r.Rijen.Should().HaveCount(160, "wat wél binnenkwam blijft bruikbaar");
    }

    [Fact]
    public async Task HaalVolledig_SplitsingLevertMinderDanDeEersteAanroep_GebruiktDeEersteEnIsOnvolledig()
    {
        var r = await ProgrammaAfkapping.HaalVolledigAsync(Url, Dataservice(Reeks(1, 200), Reeks(1, 10), Reeks(11, 10)));

        r.Rijen.Should().HaveCount(100);
        r.Volledig.Should().BeFalse();
    }

    [Fact]
    public async Task HaalVolledig_FoutInEenSplitsing_PropageertDeFout()
    {
        Func<string, Task<IReadOnlyList<int>>> haal = url => url.Contains("uit=JA")
            ? throw new HttpRequestException("storing")
            : Task.FromResult(Reeks(1, 100));

        var actie = () => ProgrammaAfkapping.HaalVolledigAsync(Url, haal);

        await actie.Should().ThrowAsync<HttpRequestException>();
    }
}
