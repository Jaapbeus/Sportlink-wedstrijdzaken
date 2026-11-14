using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using Xunit;

namespace Planner.Endpoints.Tests.Email;

public sealed partial class EmailBerichtVerwerkingTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("HerplanVerzoek", "HerplanVerzoek")]
    public async Task Correctie_BewaartOorspronkelijkeEnNieuweGegevensZonderOmwisseling(string? type, string verwacht)
    {
        var p = new Poort { IsCorrectie = true, OrigineelType = type };

        await Verwerk(p);

        p.AiInvoer.Should().Be(("bericht", verwacht, "samenvatting"));
        p.Correctie.Should().Be((7, 12, verwacht, "juist", "samenvatting", "correctie"));
        p.Gebeurtenissen.Should().ContainInOrder("status-reply", "ai-correctie", "correctie-opslag", "status-geclassificeerd");
    }

    [Fact]
    public async Task GeenCorrectie_BewaartGeenLeermoment()
    {
        var p = new Poort { IsCorrectie = false };

        await Verwerk(p);

        p.Correctie.Should().BeNull();
        p.Gebeurtenissen.Should().Contain("status-geclassificeerd");
    }

    [Theory]
    [InlineData("planner-response")]
    [InlineData("certainty")]
    [InlineData("reply")]
    public async Task ReplyFlowFout_BewaartMislukteTraceEnGeeftOrigineleFoutDoor(string stap)
    {
        var p = new Poort { FoutBij = stap };

        var ex = await Record.ExceptionAsync(() => Verwerk(p));

        ex.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(stap);
        p.Gebeurtenissen.Should().ContainInOrder(stap, "onbekende-teams", "trace");
        p.Trace!.Stappen.Should().Contain(s => s.Code == TraceCodes.VerwerkingFout);
        p.Gebeurtenissen.Count(s => s == "trace").Should().Be(1);
    }

    [Fact]
    public async Task NotificatieFout_LigtBuitenTraceEnSlaatGeenTweedeTraceOp()
    {
        var p = new Poort { Uitkomst = ReplyVerwerkingUitkomst.AntwoordVerstuurd, NotificationFout = true };

        var ex = await Record.ExceptionAsync(() => Verwerk(p, Herplan()));

        ex.Should().BeOfType<InvalidOperationException>();
        p.Gebeurtenissen.Should().ContainInOrder("trace", "notificatie");
        p.Trace!.Stappen.Should().NotContain(s => s.Code == TraceCodes.VerwerkingFout);
        p.Gebeurtenissen.Count(s => s == "trace").Should().Be(1);
    }

    [Theory]
    [InlineData(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord)]
    [InlineData(ReplyVerwerkingUitkomst.VerzendFout)]
    [InlineData(ReplyVerwerkingUitkomst.OnbekendeVerzendUitkomst)]
    public async Task NietVerstuurd_StuurtGeenInterneNotificatie(ReplyVerwerkingUitkomst uitkomst)
    {
        var p = new Poort { Uitkomst = uitkomst };
        await Verwerk(p, Herplan());
        p.Gebeurtenissen.Should().NotContain("notificatie");
    }

    [Theory]
    [InlineData(VerzoekType.HerplanVerzoek, null, "2026-11-14")]
    [InlineData(VerzoekType.HerplanVerzoek, " ", "2026-11-14")]
    [InlineData(VerzoekType.HerplanVerzoek, "JO13-1", null)]
    [InlineData(VerzoekType.HerplanVerzoek, "JO13-1", " ")]
    [InlineData(VerzoekType.BeschikbaarheidCheck, "JO13-1", "2026-11-14")]
    public async Task NietVolledigHerplanVerzoek_StuurtGeenInterneNotificatie(VerzoekType type, string? team, string? datum)
    {
        var p = new Poort { Uitkomst = ReplyVerwerkingUitkomst.AntwoordVerstuurd };
        await Verwerk(p, new() { Type = type, TeamNaam = team, Datum = datum });
        p.Gebeurtenissen.Should().NotContain("notificatie");
    }

    [Fact]
    public async Task BuitenScope_LabelFoutEnTraceFoutZijnNietFataalEnGeenOnbekendeTeamOpslag()
    {
        var p = new Poort { KorteTraceFout = true };
        var graph = new Mock<IReplyGraph>(MockBehavior.Strict);
        graph.Setup(g => g.EnsureMasterCategoryAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("label"));
        p.GraphValue = graph.Object;

        await Verwerk(p, new() { Type = VerzoekType.BuitenScope });

        p.Gebeurtenissen.Should().ContainInOrder("status-buiten-scope", "korte-trace");
        p.Gebeurtenissen.Should().NotContain("antwoordlimiet", "onbekende-teams", "trace", "reply");
        graph.VerifyAll();
        graph.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TemplateWordtAlleenOpVerzoekVanReplyBeleidGebouwd()
    {
        var p = new Poort { TemplateOpvragen = true };
        await Verwerk(p);
        p.Gebeurtenissen.Should().ContainInOrder("certainty", "reply", "template", "trace");
    }

    private static BerichtClassificatie Herplan()
        => new() { Type = VerzoekType.HerplanVerzoek, TeamNaam = "JO13-1", Datum = "2026-11-14" };

    private static Task Verwerk(Poort poort, BerichtClassificatie? classificatie = null)
        => EmailBerichtVerwerking.VerwerkAsync("bericht", classificatie ?? new(), GeenAdressen, NullLogger.Instance, poort);
}
