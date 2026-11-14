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
    private static readonly IReadOnlySet<string> GeenAdressen = new HashSet<string>();

    [Fact]
    public async Task UitgeslotenBericht_MarkeertGelezenEnStoptVoorOpslag()
    {
        var p = new Poort();

        await EmailBerichtVerwerking.VerwerkAsync("bericht", new(), new HashSet<string> { "afzender" }, NullLogger.Instance, p);

        p.Gebeurtenissen.Should().Equal("gelezen");
    }

    [Fact]
    public async Task GeenVerwerkingId_StoptNaIdempotentieBesluit()
    {
        var p = new Poort { VerwerkingId = null };

        await EmailBerichtVerwerking.VerwerkAsync("bericht", new(), GeenAdressen, NullLogger.Instance, p);

        p.Gebeurtenissen.Should().Equal("id");
    }

    [Fact]
    public async Task BuitenScope_SlaatPlannerOverEnBewaartKorteTraceNaMailboxacties()
    {
        var p = new Poort();
        var graph = new Mock<IReplyGraph>();
        graph.Setup(g => g.EnsureMasterCategoryAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => p.Gebeurtenissen.Add("label-aanmaken")).Returns(Task.CompletedTask);
        graph.Setup(g => g.SetCategoriesAsync(It.IsAny<string>(), It.IsAny<string[]>()))
            .Callback(() => p.Gebeurtenissen.Add("label-zetten")).Returns(Task.CompletedTask);
        graph.Setup(g => g.MarkAsReadAsync(It.IsAny<string>()))
            .Callback(() => p.Gebeurtenissen.Add("gelezen")).Returns(Task.CompletedTask);
        p.GraphValue = graph.Object;

        await EmailBerichtVerwerking.VerwerkAsync("bericht",
            new BerichtClassificatie { Type = VerzoekType.BuitenScope }, GeenAdressen, NullLogger.Instance, p);

        p.Gebeurtenissen.Should().ContainInOrder("status-buiten-scope", "label-aanmaken", "label-zetten", "gelezen", "korte-trace");
        p.Gebeurtenissen.Should().NotContain("antwoordlimiet", "planner", "reply", "notificatie");
    }

    [Fact]
    public async Task Antwoordlimiet_StoptVoorGeclassificeerdStatusEnPlanner()
    {
        var p = new Poort { AntwoordLimiet = true };

        await EmailBerichtVerwerking.VerwerkAsync("bericht",
            new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek, TeamNaam = "team", Datum = "2026-11-14" },
            GeenAdressen, NullLogger.Instance, p);

        p.Gebeurtenissen.Should().Equal("id", "bepaal-reply", "status-reply", "ai-correctie", "antwoordlimiet");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task LegeConversationId_SlaatReplydetectieOver(string? conversationId)
    {
        var p = new Poort { ConversationIdValue = conversationId };

        await EmailBerichtVerwerking.VerwerkAsync("bericht", new(), GeenAdressen, NullLogger.Instance, p);

        p.Gebeurtenissen.Should().NotContain("bepaal-reply", "status-reply", "ai-correctie", "correctie-opslag");
    }

    [Fact]
    public async Task SuccesvolleVerwerking_HoudtFasevolgordeEnStuurtNotificatieNaTrace()
    {
        var p = new Poort { Uitkomst = ReplyVerwerkingUitkomst.AntwoordVerstuurd };

        await EmailBerichtVerwerking.VerwerkAsync("bericht",
            new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek, TeamNaam = "team", Datum = "2026-11-14" },
            GeenAdressen, NullLogger.Instance, p);

        p.Gebeurtenissen.Should().Equal("id", "bepaal-reply", "status-reply", "ai-correctie", "antwoordlimiet", "status-geclassificeerd",
            "planner", "planner-response", "status-verwerkt", "certainty", "reply", "onbekende-teams", "trace", "notificatie");
        p.TemplateGemaakt.Should().BeFalse();
    }

    [Fact]
    public async Task HandmatigeTeambeoordeling_SlaatReplyEnLazyInstellingenOver()
    {
        var p = new Poort();
        var classificatie = new BerichtClassificatie { TeamHandmatigBeoordelen = true };
        var graph = new Mock<IReplyGraph>();
        graph.Setup(g => g.EnsureMasterCategoryAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        graph.Setup(g => g.SetCategoriesAsync(It.IsAny<string>(), It.IsAny<string[]>())).Returns(Task.CompletedTask);
        graph.Setup(g => g.MarkAsReadAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        var opslag = new Mock<IReplyPersistentie>();
        opslag.Setup(s => s.UpdateStatusAsync(It.IsAny<int>(), It.IsAny<EmailStatus>(), It.IsAny<string?>())).Returns(Task.CompletedTask);
        p.GraphValue = graph.Object;
        p.PersistentieValue = opslag.Object;

        await EmailBerichtVerwerking.VerwerkAsync("bericht", classificatie, GeenAdressen, NullLogger.Instance, p);

        p.Gebeurtenissen.Should().ContainInOrder("antwoordlimiet", "status-geclassificeerd", "planner", "planner-response", "onbekende-teams", "trace");
        p.Gebeurtenissen.Should().NotContain("certainty", "reply", "template", "notificatie", "status-verwerkt");
        p.TemplateGemaakt.Should().BeFalse();
    }

    [Fact]
    public async Task PlannerFout_BewaartEénTrace_BeheertOrigineleFout_EnStuurtGeenNotificatie()
    {
        var fout = new InvalidOperationException("planner");
        var p = new Poort { PlannerFout = fout };

        var ontvangen = await Record.ExceptionAsync(() =>
            EmailBerichtVerwerking.VerwerkAsync("bericht", new(), GeenAdressen, NullLogger.Instance, p));

        ontvangen.Should().BeSameAs(fout);
        p.Gebeurtenissen.Count(e => e == "trace").Should().Be(1);
        p.Gebeurtenissen.Should().NotContain("reply", "notificatie");
    }

    [Fact]
    public async Task TraceOpslagFout_BewaartEenmaalEnVerandertPlannerFoutNiet()
    {
        var fout = new InvalidOperationException("planner");
        var p = new Poort { PlannerFout = fout, TraceFout = true };

        var ontvangen = await Record.ExceptionAsync(() =>
            EmailBerichtVerwerking.VerwerkAsync("bericht", new(), GeenAdressen, NullLogger.Instance, p));

        ontvangen.Should().BeSameAs(fout);
        p.Gebeurtenissen.Count(e => e == "trace").Should().Be(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ReplyCorrectie_AlleenAiOfInsertFoutWordtOvergeslagen(bool aiFout, bool insertFout)
    {
        var p = new Poort { AiFout = aiFout, InsertFout = insertFout, IsCorrectie = true };

        await EmailBerichtVerwerking.VerwerkAsync("bericht",
            new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek, TeamNaam = "team", Datum = "2026-11-14" },
            GeenAdressen, NullLogger.Instance, p);

        p.Gebeurtenissen.Should().Contain("status-reply");
        p.Gebeurtenissen.Should().Contain("ai-correctie");
        if (aiFout)
            p.Gebeurtenissen.Should().NotContain("correctie-opslag");
        else
            p.Gebeurtenissen.Should().Contain("correctie-opslag");
        p.Gebeurtenissen.Should().Contain("status-geclassificeerd");
    }

    [Fact]
    public async Task ReplyLookupFout_PropagatesEnGeenTraceWordtAangemaakt()
    {
        var p = new Poort { ReplyLookupFout = true };

        var ontvangen = await Record.ExceptionAsync(() =>
            EmailBerichtVerwerking.VerwerkAsync("bericht", new(), GeenAdressen, NullLogger.Instance, p));

        ontvangen.Should().BeOfType<InvalidOperationException>();
        p.Gebeurtenissen.Should().Equal("id", "bepaal-reply");
    }

    [Fact]
    public async Task ReplyStatusFout_PropagatesEnGeenTraceWordtAangemaakt()
    {
        var p = new Poort { ReplyStatusFout = true };
        var ontvangen = await Record.ExceptionAsync(() =>
            EmailBerichtVerwerking.VerwerkAsync("bericht", new(), GeenAdressen, NullLogger.Instance, p));

        ontvangen.Should().BeOfType<InvalidOperationException>();
        p.Gebeurtenissen.Should().Equal("id", "bepaal-reply", "status-reply");
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, null)]
    public async Task GeenReplyOfGeenOrigineleRij_SlaatCorrectieDetectieOver(bool isReply, int? origineelId)
    {
        var p = new Poort { IsReply = isReply, OrigineelId = origineelId };
        await EmailBerichtVerwerking.VerwerkAsync("bericht", new(), GeenAdressen, NullLogger.Instance, p);

        p.Gebeurtenissen.Should().NotContain("status-reply", "ai-correctie", "correctie-opslag");
    }

    [Fact]
    public async Task NotificatieWordtAlleenNaVerstuurdAntwoordUitgevoerd()
    {
        var p = new Poort { Uitkomst = ReplyVerwerkingUitkomst.VerzendFout };

        await EmailBerichtVerwerking.VerwerkAsync("bericht",
            new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek, TeamNaam = "team", Datum = "2026-11-14" },
            GeenAdressen, NullLogger.Instance, p);

        p.Gebeurtenissen.Should().NotContain("notificatie");
        p.Gebeurtenissen.Should().Contain("trace");
    }

    private sealed class Poort : IEmailBerichtPoort<string>
    {
        public List<string> Gebeurtenissen { get; } = [];
        public bool AntwoordLimiet { get; init; }
        public int? VerwerkingId { get; init; } = 12;
        public ReplyVerwerkingUitkomst Uitkomst { get; init; } = ReplyVerwerkingUitkomst.AfgerondZonderAntwoord;
        public bool IsCorrectie { get; init; }
        public bool AiFout { get; init; }
        public bool InsertFout { get; init; }
        public bool ReplyLookupFout { get; init; }
        public bool ReplyStatusFout { get; init; }
        public bool IsReply { get; init; } = true;
        public int? OrigineelId { get; init; } = 7;
        public string? ConversationIdValue { get; init; } = "gesprek";
        public bool TraceFout { get; init; }
        public bool NotificationFout { get; init; }
        public Exception? PlannerFout { get; init; }
        public string? OrigineelType { get; init; } = "origineel";
        public (string Bericht, string Type, string? Samenvatting)? AiInvoer { get; private set; }
        public (int OrigineelId, int Id, string Type, string? JuistType, string? Oud, string? Nieuw)? Correctie { get; private set; }
        public BeslissingsTrace? Trace { get; private set; }
        public bool KorteTraceFout { get; init; }
        public string? FoutBij { get; init; }
        public bool TemplateOpvragen { get; init; }
        public bool TemplateGemaakt { get; private set; }
        public IReplyGraph GraphValue { get; set; } = DefaultGraph();
        public IReplyPersistentie PersistentieValue { get; set; } = new Mock<IReplyPersistentie>().Object;
        public IReplyGraph Graph => GraphValue;
        public IReplyPersistentie Persistentie => PersistentieValue;
        public Func<string, string> SanitizeFoutMelding => value => value;
        public string MessageId(string bericht) => bericht;
        public string Afzender(string bericht) => "afzender";
        public string? ConversationId(string bericht) => ConversationIdValue;
        public Task MarkeerGelezenAsync(string messageId) { Gebeurtenissen.Add("gelezen"); return Task.CompletedTask; }
        public Task<int?> BepaalVerwerkingIdAsync(string bericht, Microsoft.Extensions.Logging.ILogger log)
        { Gebeurtenissen.Add("id"); return Task.FromResult(VerwerkingId); }
        public Task<(bool IsReply, int? OrigineelId, string? OrigineelType, string? OrigineleSamenvatting)> DetecteerReplyAsync(string conversationId, Microsoft.Extensions.Logging.ILogger log)
        {
            Gebeurtenissen.Add("bepaal-reply");
            if (ReplyLookupFout) throw new InvalidOperationException("lookup");
            return Task.FromResult((IsReply, OrigineelId, OrigineelType, (string?)"samenvatting"));
        }
        public Task UpdateReplyStatusAsync(int verwerkingId, int origineelId) { Gebeurtenissen.Add("status-reply"); if (ReplyStatusFout) throw new InvalidOperationException("reply status"); return Task.CompletedTask; }
        public Task<(bool IsCorrectie, string? JuistType, string? Samenvatting)> DetecteerCorrectieAsync(string bericht, string origineelType, string? origineleSamenvatting)
        {
            AiInvoer = (bericht, origineelType, origineleSamenvatting);
            Gebeurtenissen.Add("ai-correctie");
            if (AiFout) throw new InvalidOperationException("ai");
            return Task.FromResult((IsCorrectie, (string?)"juist", (string?)"correctie"));
        }
        public Task BewaarCorrectieAsync(int origineelId, int verwerkingId, string origineelType, string? juistType, string? origineleSamenvatting, string? samenvatting)
        { Correctie = (origineelId, verwerkingId, origineelType, juistType, origineleSamenvatting, samenvatting); Gebeurtenissen.Add("correctie-opslag"); if (InsertFout) throw new InvalidOperationException("insert"); return Task.CompletedTask; }
        public Task<bool> HandelBuitenScopeAsync(int verwerkingId, string bericht, BerichtClassificatie classificatie,
            string classificatieJson, Microsoft.Extensions.Logging.ILogger log)
            => EmailVerwerkingsAfhandeling.HandelBuitenScopeAsync(verwerkingId, bericht,
                classificatie.Type == VerzoekType.BuitenScope,
                () => UpdateStatusAsync(verwerkingId, EmailStatus.BuitenScope, classificatieJson),
                new Mailbox(this), log);
        private sealed class Mailbox(Poort poort) : IEmailMailboxActies
        {
            public Task MarkeerGelezenAsync(string id) => poort.Graph.MarkAsReadAsync(id);
            public async Task LabelGeenAiAntwoordAsync(string id)
            {
                await poort.Graph.EnsureMasterCategoryAsync(EmailCategorieLabels.GeenAiAntwoord, EmailCategorieLabels.GeenAiAntwoordKleur);
                await poort.Graph.SetCategoriesAsync(id, EmailCategorieLabels.GeenAiAntwoord);
            }
        }
        public Task<bool> HandelAntwoordLimietAsync(int verwerkingId, string bericht, BerichtClassificatie classificatie, string classificatieJson, Microsoft.Extensions.Logging.ILogger log)
        { Gebeurtenissen.Add("antwoordlimiet"); return Task.FromResult(AntwoordLimiet); }
        public Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? classificatieJson)
        { Gebeurtenissen.Add(status switch { EmailStatus.Geclassificeerd => "status-geclassificeerd", EmailStatus.BuitenScope => "status-buiten-scope", _ => "status-verwerkt" }); return Task.CompletedTask; }
        public Task<string> VerwerkMetPlannerAsync(string bericht, BerichtClassificatie classificatie, TraceBuilder trace, Microsoft.Extensions.Logging.ILogger log)
        { Gebeurtenissen.Add("planner"); if (PlannerFout is not null) throw PlannerFout; return Task.FromResult("{}"); }
        public Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson) { Gebeurtenissen.Add("planner-response"); GooiBij("planner-response"); return Task.CompletedTask; }
        public async Task<ReplyVerwerkingUitkomst> HandelReplyFlowAfAsync(int verwerkingId, string bericht, BerichtClassificatie classificatie, string plannerResponseJson, bool reviewMode, string? reviewRecipient, Func<Task<(string onderwerp, string body)>> bouwTemplateAsync, Func<string, string> sanitizeFoutMelding, Microsoft.Extensions.Logging.ILogger log, TraceBuilder trace, bool zekerheidspoortActief)
        { Gebeurtenissen.Add("reply"); GooiBij("reply"); if (TemplateOpvragen) await bouwTemplateAsync(); return Uitkomst; }
        public Task<bool> ZekerheidspoortActiefAsync(Microsoft.Extensions.Logging.ILogger log) { Gebeurtenissen.Add("certainty"); GooiBij("certainty"); return Task.FromResult(true); }
        public Task<(string onderwerp, string body)> BouwTemplateAntwoordAsync(string bericht, BerichtClassificatie classificatie, string plannerResponseJson, Microsoft.Extensions.Logging.ILogger log, TraceBuilder trace)
        { TemplateGemaakt = true; Gebeurtenissen.Add("template"); return Task.FromResult(("onderwerp", "body")); }
        public Task BewaarKorteTraceAsync(int verwerkingId, string verzoekType, BeslissingsTrace trace, Microsoft.Extensions.Logging.ILogger log) { Gebeurtenissen.Add("korte-trace"); if (KorteTraceFout) throw new InvalidOperationException("trace"); return Task.CompletedTask; }
        public Task BewaarTraceAsync(int verwerkingId, string verzoekType, BeslissingsTrace trace) { Trace = trace; Gebeurtenissen.Add("trace"); if (TraceFout) throw new InvalidOperationException("trace"); return Task.CompletedTask; }
        public Task BewaarOnbekendeTeamtekstenAsync(BeslissingsTrace trace, int verwerkingId, Microsoft.Extensions.Logging.ILogger log) { Gebeurtenissen.Add("onbekende-teams"); return Task.CompletedTask; }
        public Task StuurTeamleiderNotificatieAsync(BerichtClassificatie classificatie, Microsoft.Extensions.Logging.ILogger log) { Gebeurtenissen.Add("notificatie"); if (NotificationFout) throw new InvalidOperationException("notification"); return Task.CompletedTask; }

        private void GooiBij(string stap)
        {
            if (FoutBij == stap) throw new InvalidOperationException(stap);
        }

        private static IReplyGraph DefaultGraph()
        {
            var graph = new Mock<IReplyGraph>();
            graph.Setup(g => g.EnsureMasterCategoryAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
            graph.Setup(g => g.SetCategoriesAsync(It.IsAny<string>(), It.IsAny<string[]>())).Returns(Task.CompletedTask);
            graph.Setup(g => g.MarkAsReadAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
            return graph.Object;
        }
    }
}
