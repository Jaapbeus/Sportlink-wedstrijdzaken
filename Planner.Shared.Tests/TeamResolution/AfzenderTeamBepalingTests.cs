using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using Planner.Shared.TeamResolution;
using Xunit;

namespace Planner.Shared.Tests.TeamResolution;

/// <summary>
/// Gevallen van de afzender-opzoeking (#1862): het eigen team uit de teambegeleiding van de afzender. Synthetische
/// bron en resolver, geen database. Testadres uit ARCHITECTUUR.md §8.1.5.
/// </summary>
public class AfzenderTeamBepalingTests
{
    private const string Club = "ALLSTARS";
    private const string Adres = "trainer@voorbeeld.nl";
    private const string Begeleidingswaarde = "O23-1";
    private const string Canoniek = "ALLSTARS JO23-1";

    private static TeamResolutionResult Opgelost(int id, string naam) => new(id, naam, 1.0, [], ResolutionBron.ExacteAlias);

    private static BerichtClassificatie Zonder(VerzoekType type = VerzoekType.BeschikbaarheidCheck, string? team = null)
        => new() { Type = type, TeamNaam = team, Datum = "2026-11-14" };

    private static async Task<(bool Herkend, BerichtClassificatie C, TraceBuilder T, TBron B)> Voer<TBron>(
        BerichtClassificatie c, TBron bron, FakeResolver resolver, string? afzender = Adres, params string[] replyTo)
        where TBron : IAfzenderTeamBron
    {
        var trace = new TraceBuilder();
        var herkend = await EigenTeamBepaling.BereidPlannerVoorAsync(c, resolver, Club, NullLogger.Instance, trace,
            afzender: new AfzenderOpzoeking(bron, afzender, replyTo));
        return (herkend, c, trace, bron);
    }

    private static FakeResolver Resolver() => new()
    {
        [Begeleidingswaarde] = Opgelost(7, Canoniek),
        ["JO13-2"] = Opgelost(8, "ALLSTARS JO13-2"),
    };

    [Theory]
    [InlineData(VerzoekType.BeschikbaarheidCheck)]
    [InlineData(VerzoekType.HerplanVerzoek)]
    public async Task GeenTeamInMail_AfzenderBegeleidtPreciesEenTeam_LeidtHetTeamAf(VerzoekType type)
    {
        var (herkend, c, trace, _) = await Voer(Zonder(type), new FakeBron(Begeleidingswaarde), Resolver());

        herkend.Should().BeTrue();
        c.TeamNaam.Should().Be(Canoniek);
        c.TeamAfgeleidUitAfzender.Should().BeTrue();
        c.TeamNaamOnbevestigd.Should().BeFalse();
        c.LeeftijdsCategorie.Should().Be("JO23", "de categorie volgt uit het afgeleide team");
        var stap = trace.Stappen.Should().ContainSingle(s => s.Code == TraceCodes.BegeleidingAfzender).Subject;
        stap.Details[AfzenderTeamBepaling.AantalSleutel].Should().Be("1");
        stap.Details.Should().NotContainKey("canoniekeNaam", "de trace bewaart alleen het aantal teams (besluit 4A)");
        JsonConvert.SerializeObject(trace.Stappen).Should().NotContain(Canoniek);
        ZekerheidsBeoordeling.Beoordeel(trace.Stappen).IsZeker.Should().BeTrue("een afgeleid team telt als opgelost");
    }

    [Fact]
    public async Task HoofdletterVerschillenInTeamwaarde_ZijnEenTeam()
    {
        var (herkend, c, _, _) = await Voer(Zonder(), new FakeBron(Begeleidingswaarde, " o23-1 "), Resolver());

        herkend.Should().BeTrue();
        c.TeamAfgeleidUitAfzender.Should().BeTrue();
    }

    [Fact]
    public async Task AfzenderBijMeerdereTeams_KiestNietsEnNoemtDeTeamsNiet()
    {
        var (herkend, c, trace, _) = await Voer(Zonder(), new FakeBron(Begeleidingswaarde, "JO13-2"), Resolver());

        herkend.Should().BeFalse();
        c.TeamNaam.Should().BeNull();
        c.TeamAfgeleidUitAfzender.Should().BeFalse();
        c.TeamNaamOnbevestigd.Should().BeTrue();
        var stap = trace.Stappen.Should().ContainSingle(s => s.Code == TraceCodes.BegeleidingAfzender).Subject;
        stap.Details[AfzenderTeamBepaling.AantalSleutel].Should().Be("meer");
        JsonConvert.SerializeObject(trace.Stappen).Should().NotContain(Begeleidingswaarde).And.NotContain("JO13-2");
        ZekerheidsBeoordeling.Beoordeel(trace.Stappen).Redenen.Should().Contain("Meerdere teams komen in aanmerking; er is niets gekozen");
    }

    [Fact]
    public async Task AfzenderOnbekendInDeBegeleiding_BlijftNietHerkend()
    {
        var (herkend, c, trace, _) = await Voer(Zonder(), new FakeBron(), Resolver());

        herkend.Should().BeFalse();
        c.TeamAfgeleidUitAfzender.Should().BeFalse();
        trace.Stappen.Single(s => s.Code == TraceCodes.BegeleidingAfzender).Details[AfzenderTeamBepaling.AantalSleutel].Should().Be("0");
        ZekerheidsBeoordeling.Beoordeel(trace.Stappen).IsZeker.Should().BeFalse();
    }

    [Fact]
    public async Task TeamwaardeDieNietOplost_TeltAlsNietGevonden()
    {
        var (herkend, c, trace, _) = await Voer(Zonder(), new FakeBron("onbekend-team"), Resolver());

        herkend.Should().BeFalse();
        c.TeamAfgeleidUitAfzender.Should().BeFalse();
        c.TeamNaam.Should().BeNull();
        ZekerheidsBeoordeling.Beoordeel(trace.Stappen).IsZeker.Should().BeFalse();
    }

    [Fact]
    public async Task GenoemdTeamWint_EnEenAfwijkingIsAlleenEenTracenotitie()
    {
        var (herkend, c, trace, _) = await Voer(Zonder(team: "JO13-2"), new FakeBron(Begeleidingswaarde), Resolver());

        herkend.Should().BeTrue();
        c.TeamNaam.Should().Be("ALLSTARS JO13-2");
        c.TeamAfgeleidUitAfzender.Should().BeFalse("het genoemde team wint; er is niets afgeleid");
        c.TeamHandmatigBeoordelen.Should().BeFalse("geen review-markering");
        var stap = trace.Stappen.Single(s => s.Code == TraceCodes.BegeleidingAfzender);
        stap.Details[AfzenderTeamBepaling.AfwijkendTeamSleutel].Should().Be("ja");
        JsonConvert.SerializeObject(trace.Stappen).Should().NotContain(Canoniek, "de teams van de afzender staan niet in de notitie");
        ZekerheidsBeoordeling.Beoordeel(trace.Stappen).IsZeker.Should().BeTrue();
    }

    [Fact]
    public async Task GenoemdTeamGelijkAanBegeleidingsteam_GeeftGeenAfwijkingsnotitie()
    {
        var (_, c, trace, _) = await Voer(Zonder(team: "O23-1"), new FakeBron(Begeleidingswaarde), Resolver());

        c.TeamAfgeleidUitAfzender.Should().BeFalse();
        trace.Stappen.Single(s => s.Code == TraceCodes.BegeleidingAfzender).Details.Should().NotContainKey(AfzenderTeamBepaling.AfwijkendTeamSleutel);
    }

    [Theory]
    [InlineData("andere@voorbeeld.nl")]
    [InlineData("trainer@voorbeeld.nl", "andere@voorbeeld.nl")]
    public async Task AntwoordadresWijktAfVanAfzender_GeenOpzoeking(params string[] replyTo)
    {
        var (herkend, c, trace, bron) = await Voer(Zonder(), new FakeBron(Begeleidingswaarde), Resolver(), Adres, replyTo);

        herkend.Should().BeFalse();
        bron.Aanroepen.Should().Be(0, "een afwijkend antwoordadres mag nooit een opzoeking opleveren");
        c.TeamAfgeleidUitAfzender.Should().BeFalse();
        trace.Stappen.Single(s => s.Code == TraceCodes.BegeleidingAfzender).Uitkomst.Should().Be("Overgeslagen");
    }

    [Fact]
    public async Task Proefverwerking_SlaatDeOpzoekingOverEnNoemtDeReden()
    {
        // De beheerderstester: een vrij ingetypt adres mag niet tonen welk team iemand begeleidt (buiten #1701 om).
        var (herkend, c, trace, _) = await Voer(Zonder(), OvergeslagenAfzenderTeamBron.Proefverwerking, Resolver());

        herkend.Should().BeFalse();
        c.TeamNaam.Should().BeNull();
        c.TeamAfgeleidUitAfzender.Should().BeFalse();
        var stap = trace.Stappen.Single(s => s.Code == TraceCodes.BegeleidingAfzender);
        stap.Uitkomst.Should().Be("Overgeslagen");
        stap.Details["reden"].Should().Be(OvergeslagenAfzenderTeamBron.Proefverwerking.Reden);
    }

    [Fact]
    public async Task AntwoordadresGelijkAanAfzender_ZonderHoofdletterverschil_DoetWelDeOpzoeking()
    {
        var (herkend, _, _, bron) = await Voer(Zonder(), new FakeBron(Begeleidingswaarde), Resolver(), Adres, " TRAINER@voorbeeld.NL ");

        herkend.Should().BeTrue();
        bron.Aanroepen.Should().Be(1);
    }

    [Fact]
    public async Task BronKrijgtHetGenormaliseerdeAdres()
    {
        var (_, _, _, bron) = await Voer(Zonder(), new FakeBron(Begeleidingswaarde), Resolver(), "  Trainer@Voorbeeld.NL ");

        bron.Ontvangen.Should().Be("trainer@voorbeeld.nl");
        AfzenderTeamBronHulp.NormaliseerAdres(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task GeenAfzender_GeenOpzoeking(string? afzender)
    {
        var (herkend, _, trace, bron) = await Voer(Zonder(), new FakeBron(Begeleidingswaarde), Resolver(), afzender);

        herkend.Should().BeFalse();
        bron.Aanroepen.Should().Be(0);
        trace.Stappen.Should().NotContain(s => s.Code == TraceCodes.BegeleidingAfzender);
    }

    [Fact]
    public async Task VerzoektypeZonderTeamvereiste_GeenOpzoeking()
    {
        var (_, _, _, bron) = await Voer(Zonder(VerzoekType.TeamContactOpvragen), new FakeBron(Begeleidingswaarde), Resolver());

        bron.Aanroepen.Should().Be(0);
    }

    [Fact]
    public async Task StoringInDeBron_IsNietGevondenZonderFoutOfAdresInDeLog()
    {
        var logger = new VangLogger();
        var c = Zonder();
        var trace = new TraceBuilder();

        var herkend = await EigenTeamBepaling.BereidPlannerVoorAsync(c, Resolver(), Club, logger, trace,
            afzender: new AfzenderOpzoeking(new FakeBron { Gooit = true }, Adres, []));

        herkend.Should().BeFalse();
        c.TeamAfgeleidUitAfzender.Should().BeFalse();
        logger.Regels.Should().NotBeEmpty().And.OnlyContain(r => !r.Contains(Adres) && !r.Contains("voorbeeld"));
    }

    [Fact]
    public async Task HetAdresStaatNooitInDeTrace()
    {
        var (_, _, trace, _) = await Voer(Zonder(), new FakeBron(Begeleidingswaarde), Resolver());

        var json = JsonConvert.SerializeObject(trace.Stappen);
        json.Should().NotContainEquivalentOf("voorbeeld.nl").And.NotContainEquivalentOf("trainer@");
    }

    [Fact]
    public async Task OpnieuwBereiden_ZetDeAfleidingVlagTerugAlsHetTeamDaarnaGenoemdWordt()
    {
        var c = Zonder();
        c.TeamAfgeleidUitAfzender = true;

        await Voer(c, new FakeBron(), Resolver());

        c.TeamAfgeleidUitAfzender.Should().BeFalse();
    }

    [Fact]
    public void ControleRegel_NoemtHetAfgeleideTeam_EnZwijgtAlsHetNietIsAfgeleid()
    {
        var afgeleid = new BerichtClassificatie { TeamNaam = Canoniek, TeamAfgeleidUitAfzender = true };
        AfgeleidTeamControle.Regel(afgeleid).Should().Be($"\n\nWe gaan uit van {Canoniek}; klopt dat niet, laat het ons weten.");

        AfgeleidTeamControle.Regel(new BerichtClassificatie { TeamNaam = Canoniek }).Should().BeEmpty();
        AfgeleidTeamControle.Regel(new BerichtClassificatie { TeamNaam = Canoniek, TeamAfgeleidUitAfzender = true, TeamNaamOnbevestigd = true }).Should().BeEmpty();
        AfgeleidTeamControle.Regel(new BerichtClassificatie { TeamAfgeleidUitAfzender = true }).Should().BeEmpty();
    }

    [Fact]
    public void ReplyToUitHeaders_LeestAdressenUitDeHeader_EnNegeertAndereHeaders()
    {
        var headers = new KeyValuePair<string, string>[]
        {
            new("Subject", "x@voorbeeld.nl"),
            new("reply-to", "Jan de Vries <Trainer@voorbeeld.nl>, ander@voorbeeld.nl"),
        };

        AfzenderTeamBepaling.ReplyToUitHeaders(headers).Should().Equal("Trainer@voorbeeld.nl", "ander@voorbeeld.nl");
        AfzenderTeamBepaling.ReplyToUitHeaders(null).Should().BeEmpty();
        AfzenderTeamBepaling.ReplyToUitHeaders([]).Should().BeEmpty();
    }

    [Fact]
    public void ReplyToUitHeaders_OnleesbareHeader_TeltAlsAfwijkendAdres()
    {
        var adressen = AfzenderTeamBepaling.ReplyToUitHeaders([new("Reply-To", "geen adres <<>")]);

        adressen.Should().ContainSingle();
        var opzoeking = new AfzenderOpzoeking(new FakeBron(Begeleidingswaarde), Adres, adressen);
        EigenTeamBepaling.BereidPlannerVoorAsync(Zonder(), Resolver(), Club, NullLogger.Instance, null, afzender: opzoeking)
            .GetAwaiter().GetResult().Should().BeFalse();
    }

    private sealed class FakeBron(params string[] teams) : IAfzenderTeamBron
    {
        public int Aanroepen { get; private set; }
        public string? Ontvangen { get; private set; }
        public bool Gooit { get; init; }

        public Task<IReadOnlyList<string>> ZoekTeamsVanAfzenderAsync(string afzender, string clubCode)
        {
            Aanroepen++;
            Ontvangen = afzender;
            if (Gooit) throw new InvalidOperationException($"storing voor {afzender}");
            return Task.FromResult<IReadOnlyList<string>>(teams);
        }
    }

    private sealed class FakeResolver : Dictionary<string, TeamResolutionResult>, ITeamResolver
    {
        public Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
            => Task.FromResult(TryGetValue(request.RuweTeamTekst.Trim(), out var r) ? r : TeamResolutionResult.Onopgelost);
    }

    private sealed class VangLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Regels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Regels.Add(formatter(state, exception) + (exception?.ToString() ?? ""));
    }
}
