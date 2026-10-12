using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>Berichten van automatische afzenders worden herkend aan hun internetheaders (#1674).</summary>
public class AutomatischeAfzenderHerkenningTests
{
    private static bool Automatisch(string naam, string waarde)
        => AutomatischeAfzenderHerkenning.IsAutomatisch([new KeyValuePair<string, string>(naam, waarde)]);

    [Fact]
    public void ZonderHeaders_IsHetGeenAutomatischeAfzender()
    {
        AutomatischeAfzenderHerkenning.IsAutomatisch(null).Should().BeFalse();
        AutomatischeAfzenderHerkenning.IsAutomatisch([]).Should().BeFalse();
    }

    [Theory]
    [InlineData("Auto-Submitted", "auto-replied", true)]
    [InlineData("Auto-Submitted", "auto-generated", true)]
    [InlineData("auto-submitted", "AUTO-REPLIED; type=vacation", true)]
    [InlineData("Auto-Submitted", "no", false)]
    [InlineData("Auto-Submitted", "No", false)]
    [InlineData("Auto-Submitted", "", false)]
    public void AutoSubmitted_AllesBehalveNoIsAutomatisch(string naam, string waarde, bool verwacht)
        => Automatisch(naam, waarde).Should().Be(verwacht);

    [Theory]
    [InlineData("bulk", true)]
    [InlineData("auto_reply", true)]
    [InlineData("junk", true)]
    [InlineData(" bulk ", true)]
    [InlineData("list", false)] // mailinglijst/ticketsysteem: kan een gewoon verzoek van een mens zijn
    [InlineData("LIST", false)]
    [InlineData("first-class", false)]
    [InlineData("", false)]
    public void Precedence_OnderdrukkendeWaardenZijnAutomatisch(string waarde, bool verwacht)
        => Automatisch("Precedence", waarde).Should().Be(verwacht);

    [Theory]
    [InlineData("All")]
    [InlineData("OOF")]
    [InlineData("AutoReply")]
    [InlineData("DR, NDR, OOF")]
    public void InkomendeAutoResponseSuppress_IsGeenReden(string waarde)
        => Automatisch("X-Auto-Response-Suppress", waarde).Should().BeFalse(
            "die header vraagt de ontvanger om geen autoreply en zegt niets over of de afzender een mens is");

    [Fact]
    public void OnbekendeHeadersZijnGeenReden()
        => Automatisch("X-Mailer", "Outlook").Should().BeFalse();

    [Fact]
    public void EenAutomatischeHeaderTussenGewoneHeadersVoldoet()
    {
        var headers = new[]
        {
            new KeyValuePair<string, string>("Received", "from x"),
            new KeyValuePair<string, string>("Auto-Submitted", "auto-replied"),
            new KeyValuePair<string, string>("Subject", "Afwezig"),
        };

        AutomatischeAfzenderHerkenning.IsAutomatisch(headers).Should().BeTrue();
    }

    [Fact]
    public void UitgaandeHeaders_OnderdrukkenAlleenAfwezigheidsmeldingenEnAutoreplies()
    {
        var header = AutomatischeAfzenderHerkenning.UitgaandeHeaders.Should().ContainSingle().Subject;

        header.Key.Should().Be("X-Auto-Response-Suppress");
        header.Value.Should().Be("OOF, AutoReply");

        // Onbestelbaarheids- en bezorgberichten moeten de coördinator blijven bereiken.
        var tokens = header.Value.Split(',', StringSplitOptions.TrimEntries);
        tokens.Should().NotContain(t => t.Equals("All", StringComparison.OrdinalIgnoreCase)
            || t.Equals("NDR", StringComparison.OrdinalIgnoreCase)
            || t.Equals("DR", StringComparison.OrdinalIgnoreCase)
            || t.Equals("RN", StringComparison.OrdinalIgnoreCase)
            || t.Equals("NRN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HerkenningsHeader_NoemtAlleenDeHeadernaam()
    {
        AutomatischeAfzenderHerkenning.HerkenningsHeader([new("auto-submitted", "auto-replied; geheim")])
            .Should().Be("Auto-Submitted");
        AutomatischeAfzenderHerkenning.HerkenningsHeader([new("Precedence", "bulk")]).Should().Be("Precedence");
        AutomatischeAfzenderHerkenning.HerkenningsHeader([new("Precedence", "list")]).Should().BeNull();
        AutomatischeAfzenderHerkenning.HerkenningsHeader(null).Should().BeNull();
    }
}

/// <summary>
/// Een overgeslagen bericht van een automatische afzender verdwijnt niet zonder spoor: het krijgt het
/// label "Geen AI antwoord" en een logregel; mislukt het labelen, dan blijft het ongelezen (#1674).
/// </summary>
public class HandelAutomatischeAfzenderTests
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

    private static readonly KeyValuePair<string, string>[] Headers = [new("Auto-Submitted", "auto-replied")];

    [Fact]
    public async Task LabeltEnMarkeertAlsGelezen()
    {
        var mailbox = new OpnemendeMailbox();
        var log = new OpvangendeLogger();

        await EmailVerwerkingsAfhandeling.HandelAutomatischeAfzenderAsync("m1", Headers, mailbox, log);

        mailbox.Aanroepen.Should().Equal("label:m1", "gelezen:m1");
        log.Regels.Should().ContainSingle().Which.Should().Contain("Auto-Submitted").And.Contain("Geen AI antwoord");
    }

    [Fact]
    public async Task LabelenMislukt_BlijftOngelezenEnGooitNiet()
    {
        var mailbox = new OpnemendeMailbox { LabelGooit = true };

        await EmailVerwerkingsAfhandeling.HandelAutomatischeAfzenderAsync("m1", Headers, mailbox, NullLogger.Instance);

        mailbox.Aanroepen.Should().BeEmpty("een volgende poll probeert het opnieuw, zonder AI-aanroep");
    }
}
