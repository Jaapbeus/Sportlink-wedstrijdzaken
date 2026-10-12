using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>Antwoordlusbeveiliging: limiet per conversatie en geen antwoord op een bevestiging op ons antwoord (#1674).</summary>
public class AntwoordLimietTests
{
    [Fact]
    public void EersteBericht_GaatDoor()
        => AntwoordLimiet.Beoordeel(0, isBevestiging: false).Should().Be(AntwoordLimietUitkomst.Doorgaan);

    [Fact]
    public void NieuweVraagInBestaandeConversatie_GaatDoorBinnenDeLimiet()
        => AntwoordLimiet.Beoordeel(AntwoordLimiet.MaxAutomatischeAntwoordenPerConversatie - 1, isBevestiging: false)
            .Should().Be(AntwoordLimietUitkomst.Doorgaan);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BijDeLimiet_WordtNietMeerBeantwoord(bool isBevestiging)
        => AntwoordLimiet.Beoordeel(AntwoordLimiet.MaxAutomatischeAntwoordenPerConversatie, isBevestiging)
            .Should().Be(AntwoordLimietUitkomst.ConversatieLimietBereikt);

    [Fact]
    public void BevestigingZonderEerderAntwoord_GaatDoor()
        => AntwoordLimiet.Beoordeel(0, isBevestiging: true).Should().Be(AntwoordLimietUitkomst.Doorgaan);

    [Fact]
    public void BevestigingOpOnsAntwoord_WordtNietBeantwoord()
        => AntwoordLimiet.Beoordeel(1, isBevestiging: true).Should().Be(AntwoordLimietUitkomst.BevestigingOpOnsAntwoord);

    [Fact]
    public void RedenBestaatAlleenBijOverslaan()
    {
        AntwoordLimiet.Reden(AntwoordLimietUitkomst.Doorgaan).Should().BeNull();
        AntwoordLimiet.Reden(AntwoordLimietUitkomst.ConversatieLimietBereikt).Should().Be(AntwoordLimiet.RedenConversatieLimiet);
        AntwoordLimiet.Reden(AntwoordLimietUitkomst.BevestigingOpOnsAntwoord).Should().Be(AntwoordLimiet.RedenBevestigingOpAntwoord);
    }
}

/// <summary>Begrenzing van de berichttekst vóór de AI-aanroep (#1674).</summary>
public class BerichtBodyBegrenzingTests
{
    [Fact]
    public void KorteBodyBlijftOngewijzigd()
        => BerichtBodyBegrenzing.Kap("kort bericht").Should().Be("kort bericht");

    [Fact]
    public void LegeBodyWordtLegeTekst()
    {
        BerichtBodyBegrenzing.Kap(null).Should().BeEmpty();
        BerichtBodyBegrenzing.Kap("").Should().BeEmpty();
    }

    [Fact]
    public void LangeBodyWordtAfgekaptMetMarkering()
    {
        var kap = BerichtBodyBegrenzing.Kap(new string('x', BerichtBodyBegrenzing.MaxBodyTekens + 500));

        kap.Should().EndWith(BerichtBodyBegrenzing.AfkapMarkering);
        kap.Length.Should().Be(BerichtBodyBegrenzing.MaxBodyTekens + BerichtBodyBegrenzing.AfkapMarkering.Length);
    }

    [Fact]
    public void BodyPreciesOpDeGrensBlijftOngewijzigd()
    {
        var body = new string('x', BerichtBodyBegrenzing.MaxBodyTekens);
        BerichtBodyBegrenzing.Kap(body).Should().Be(body);
    }

    [Fact]
    public void AfkappenLaatGeenHalfSurrogaatpaarAchter()
    {
        var body = new string('x', 9) + "😀" + new string('y', 20); // emoji op index 9-10
        var kap = BerichtBodyBegrenzing.Kap(body, maxTekens: 10);

        kap.Should().Be(new string('x', 9) + BerichtBodyBegrenzing.AfkapMarkering);
    }
}

/// <summary>Afhandeling van een bericht dat door de antwoordlusbeveiliging wordt overgeslagen (#1674).</summary>
public class HandelAntwoordLimietTests
{
    private sealed class OpnemendeMailbox : IEmailMailboxActies
    {
        public bool LabelGooit { get; set; }
        public List<string> Aanroepen { get; } = new();

        public Task MarkeerGelezenAsync(string messageId) { Aanroepen.Add($"gelezen:{messageId}"); return Task.CompletedTask; }

        public Task LabelGeenAiAntwoordAsync(string messageId)
        {
            if (LabelGooit) throw new InvalidOperationException("graph weg");
            Aanroepen.Add($"label:{messageId}");
            return Task.CompletedTask;
        }
    }

    private static Task<bool> Handel(
        int eerder, bool isBevestiging, OpnemendeMailbox mailbox, List<string> vastgelegd, string? conversationId = "c-1",
        List<string>? traces = null)
        => EmailVerwerkingsAfhandeling.HandelAntwoordLimietAsync(
            new AntwoordLimietInvoer(7, "m-1", conversationId, "BeschikbaarheidCheck", "JO13-2", null, 1, "14:30"),
            isBevestiging, () => Task.FromResult(eerder),
            () => { vastgelegd.Add("status"); return Task.CompletedTask; },
            trace => { traces?.Add(trace.Stappen.SelectMany(st => st.Details.Values).Single(v => v.Contains("antwoord"))); return Task.CompletedTask; },
            mailbox, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    [Fact]
    public async Task BinnenDeLimiet_DoetNiets()
    {
        var mailbox = new OpnemendeMailbox();
        var vastgelegd = new List<string>();

        (await Handel(1, false, mailbox, vastgelegd)).Should().BeFalse();

        vastgelegd.Should().BeEmpty();
        mailbox.Aanroepen.Should().BeEmpty();
    }

    [Fact]
    public async Task LimietBereikt_LegtRedenVastLabeltEnMarkeertAlsGelezen()
    {
        var mailbox = new OpnemendeMailbox();
        var vastgelegd = new List<string>();

        (await Handel(AntwoordLimiet.MaxAutomatischeAntwoordenPerConversatie, false, mailbox, vastgelegd)).Should().BeTrue();

        vastgelegd.Should().Equal("status");
        mailbox.Aanroepen.Should().Equal("label:m-1", "gelezen:m-1");
    }

    [Fact]
    public async Task BevestigingOpOnsAntwoord_WordtOvergeslagen()
    {
        var mailbox = new OpnemendeMailbox();
        var vastgelegd = new List<string>();

        var traces = new List<string>();

        (await Handel(1, true, mailbox, vastgelegd, traces: traces)).Should().BeTrue();

        vastgelegd.Should().Equal("status");
        traces.Should().Equal(AntwoordLimiet.RedenBevestigingOpAntwoord);
    }

    [Fact]
    public async Task ZonderConversatieId_KanDeLimietNietWordenToegepast()
    {
        var mailbox = new OpnemendeMailbox();
        var vastgelegd = new List<string>();

        (await Handel(99, false, mailbox, vastgelegd, conversationId: " ")).Should().BeFalse();

        vastgelegd.Should().BeEmpty();
    }

    [Fact]
    public async Task LabelenMislukt_IsNietFataalWantDeStatusStaatVast()
    {
        var mailbox = new OpnemendeMailbox { LabelGooit = true };
        var vastgelegd = new List<string>();

        (await Handel(AntwoordLimiet.MaxAutomatischeAntwoordenPerConversatie, false, mailbox, vastgelegd)).Should().BeTrue();

        vastgelegd.Should().HaveCount(1);
    }
}
