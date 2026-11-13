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

    // ── #1915: terugval per team als ook thuis of uit nog afgekapt is ──

    /// <summary>Elke wedstrijd hoort bij een team; de dataservice kent thuis/uit en <c>teamcode</c> als filter.</summary>
    private static Func<string, Task<IReadOnlyList<int>>> PerTeamDataservice(
        IReadOnlyList<int> alles, Func<int, long> teamVan, Func<int, bool> isThuis, List<string>? aanroepen = null)
        => url =>
        {
            aanroepen?.Add(url);
            IEnumerable<int> selectie = alles;
            if (url.Contains("thuis=JA&uit=NEE")) selectie = alles.Where(isThuis);
            else if (url.Contains("thuis=NEE&uit=JA")) selectie = alles.Where(w => !isThuis(w));
            var teamDeel = url.Split('&').FirstOrDefault(d => d.StartsWith("teamcode=", StringComparison.Ordinal));
            if (teamDeel != null) selectie = selectie.Where(w => teamVan(w) == long.Parse(teamDeel["teamcode=".Length..]));
            return Task.FromResult<IReadOnlyList<int>>(selectie.Take(ProgrammaAfkapping.Plafond).ToList());
        };

    private static Func<Task<IReadOnlyList<long>>> Teams(int aantal)
        => () => Task.FromResult<IReadOnlyList<long>>(Enumerable.Range(0, aantal).Select(i => (long)i).ToList());

    [Fact]
    public async Task HaalVolledig_ThuisEnUitNogAfgekapt_ValtTerugOpPerTeam_EnIsVolledig()
    {
        // 300 wedstrijden: 150 thuis en 150 uit — beide boven het plafond — verdeeld over 30 teams van elk 10.
        var alles = Reeks(1, 300);
        var aanroepen = new List<string>();

        var r = await ProgrammaAfkapping.HaalVolledigAsync(Url,
            PerTeamDataservice(alles, w => w % 30, w => w % 2 == 1, aanroepen), Teams(30));

        r.Rijen.Distinct().Should().HaveCount(300, "elke wedstrijd komt via zijn team binnen");
        r.Volledig.Should().BeTrue();
        (r.Gesplitst, r.PerTeam).Should().Be((true, true));
        aanroepen.Count(a => a.Contains("teamcode=")).Should().Be(30);
        aanroepen.Where(a => a.Contains("teamcode=")).Should().OnlyContain(a => a.StartsWith(Url), "de venster-query blijft behouden");
    }

    [Fact]
    public async Task HaalVolledig_PerTeamNogAfgekapt_IsOnvolledig()
    {
        // Eén team met 150 wedstrijden: ook per team is het plafond geraakt.
        var alles = Reeks(1, 300);

        var r = await ProgrammaAfkapping.HaalVolledigAsync(Url, PerTeamDataservice(alles, w => w <= 150 ? 0 : 1, w => w % 2 == 1), Teams(2));

        r.Volledig.Should().BeFalse();
        r.PerTeam.Should().BeTrue();
    }

    [Fact]
    public async Task HaalVolledig_ZonderTeamcodes_BlijftBijDeSplitsing_EnIsOnvolledig()
    {
        var alles = Reeks(1, 300);
        var aanroepen = new List<string>();

        var r = await ProgrammaAfkapping.HaalVolledigAsync(Url, PerTeamDataservice(alles, w => w % 30, w => w % 2 == 1, aanroepen), Teams(0));

        r.Volledig.Should().BeFalse();
        r.PerTeam.Should().BeFalse();
        aanroepen.Should().NotContain(a => a.Contains("teamcode="));
    }

    [Fact]
    public async Task HaalVolledig_PerTeamLevertMinderDanDeSplitsing_BehoudtDeSplitsing()
    {
        // Een dataservice die teamcode negeert maar wel 0 rijen geeft voor elk team: niet vertrouwen.
        var alles = Reeks(1, 300);
        Func<string, Task<IReadOnlyList<int>>> haal = url =>
        {
            if (url.Contains("teamcode=")) return Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
            return PerTeamDataservice(alles, w => w % 30, w => w % 2 == 1)(url);
        };

        var r = await ProgrammaAfkapping.HaalVolledigAsync(Url, haal, Teams(30));

        r.Rijen.Should().HaveCount(200, "de splitsing leverde 2 × 100 op en blijft het beste wat we hebben");
        r.Volledig.Should().BeFalse();
    }

    [Fact]
    public async Task HaalVolledig_ONDERHetPlafond_VraagNooitDeTeamcodesOp()
    {
        var opgevraagd = false;
        Func<Task<IReadOnlyList<long>>> teams = () => { opgevraagd = true; return Task.FromResult<IReadOnlyList<long>>([1L]); };

        await ProgrammaAfkapping.HaalVolledigAsync(Url, Dataservice(Reeks(1, 50), [], []), teams);

        opgevraagd.Should().BeFalse("de teamlijst is alleen nodig voor de laatste terugval");
    }
}
