using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>
/// Tier-onafhankelijke afhandeling rond de verwerkingsrij (#1650): welke opslag- en mailboxacties
/// bij welk besluit horen. De tiers houden hun eigen integratietests op de echte persistentie.
/// </summary>
public class EmailVerwerkingsAfhandelingTests
{
    private const string MessageId = "m-allstars-1";

    private sealed class GelijktijdigException : Exception;

    private sealed class OpnemendeOpslag : IEmailVerwerkingOpslag
    {
        public EmailVerwerkingStandInfo? Stand { get; set; }
        public bool InsertGooitGelijktijdig { get; set; }
        public bool HaalStandGooit { get; set; }
        public int NieuwId { get; set; } = 77;
        public List<string> Aanroepen { get; } = new();

        public Task<EmailVerwerkingStandInfo?> HaalStandOpAsync()
            => HaalStandGooit ? throw new InvalidOperationException("db weg") : Task.FromResult(Stand);

        public Task<int> RegistreerNieuweVerwerkingAsync()
        {
            Aanroepen.Add("insert");
            return InsertGooitGelijktijdig ? throw new GelijktijdigException() : Task.FromResult(NieuwId);
        }

        public bool IsGelijktijdigeRegistratie(Exception ex) => ex is GelijktijdigException;

        public Task VerhoogPogingenAsync(int verwerkingId) { Aanroepen.Add($"verhoog:{verwerkingId}"); return Task.CompletedTask; }

        public Task UpdateFoutAsync(int verwerkingId, string foutMelding) { Aanroepen.Add($"fout:{verwerkingId}:{foutMelding}"); return Task.CompletedTask; }

        public Task ZetOpReviewAsync(int verwerkingId) { Aanroepen.Add($"review:{verwerkingId}"); return Task.CompletedTask; }
    }

    private sealed class OpnemendeMailbox : IEmailMailboxActies
    {
        public bool LabelGooit { get; set; }
        public List<string> Aanroepen { get; } = new();

        public Task MarkeerGelezenAsync(string messageId) { Aanroepen.Add($"gelezen:{messageId}"); return Task.CompletedTask; }

        public Task LabelGeenAiAntwoordAsync(string messageId)
        {
            if (LabelGooit)
                throw new InvalidOperationException("graph weg");
            Aanroepen.Add($"label:{messageId}");
            return Task.CompletedTask;
        }
    }

    private static EmailVerwerkingStandInfo Stand(VerwerkingsBesluit besluit, int pogingen = 1)
        => new(VerwerkingId: 42, Status: "Fout", Pogingen: pogingen, Besluit: besluit);

    [Fact]
    public async Task BepaalVerwerkingId_NieuwBericht_RegistreertEnGeeftNieuwId()
    {
        var opslag = new OpnemendeOpslag();
        var mailbox = new OpnemendeMailbox();

        var id = await EmailVerwerkingsAfhandeling.BepaalVerwerkingIdAsync(MessageId, opslag, mailbox, NullLogger.Instance);

        id.Should().Be(77);
        opslag.Aanroepen.Should().Equal("insert");
        mailbox.Aanroepen.Should().BeEmpty();
    }

    [Fact]
    public async Task BepaalVerwerkingId_GelijktijdigeRegistratie_StoptZonderFoutstatus()
    {
        var opslag = new OpnemendeOpslag { InsertGooitGelijktijdig = true };
        var mailbox = new OpnemendeMailbox();

        var id = await EmailVerwerkingsAfhandeling.BepaalVerwerkingIdAsync(MessageId, opslag, mailbox, NullLogger.Instance);

        id.Should().BeNull();
        opslag.Aanroepen.Should().Equal("insert");
        mailbox.Aanroepen.Should().BeEmpty("het bericht blijft ongelezen voor de andere verwerking");
    }

    [Fact]
    public async Task BepaalVerwerkingId_AndereInsertfout_WordtDoorgegeven()
    {
        var opslag = new FalendeInsertOpslag();

        var act = () => EmailVerwerkingsAfhandeling.BepaalVerwerkingIdAsync(
            MessageId, opslag, new OpnemendeMailbox(), NullLogger.Instance);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    private sealed class FalendeInsertOpslag : IEmailVerwerkingOpslag
    {
        public Task<EmailVerwerkingStandInfo?> HaalStandOpAsync() => Task.FromResult<EmailVerwerkingStandInfo?>(null);
        public Task<int> RegistreerNieuweVerwerkingAsync() => throw new TimeoutException();
        public bool IsGelijktijdigeRegistratie(Exception ex) => false;
        public Task VerhoogPogingenAsync(int verwerkingId) => Task.CompletedTask;
        public Task UpdateFoutAsync(int verwerkingId, string foutMelding) => Task.CompletedTask;
        public Task ZetOpReviewAsync(int verwerkingId) => Task.CompletedTask;
    }

    [Fact]
    public async Task BepaalVerwerkingId_AlAfgerond_MarkeertGelezenEnStopt()
    {
        var opslag = new OpnemendeOpslag { Stand = Stand(VerwerkingsBesluit.OverslaanAlAfgerond) };
        var mailbox = new OpnemendeMailbox();

        var id = await EmailVerwerkingsAfhandeling.BepaalVerwerkingIdAsync(MessageId, opslag, mailbox, NullLogger.Instance);

        id.Should().BeNull();
        opslag.Aanroepen.Should().BeEmpty();
        mailbox.Aanroepen.Should().Equal($"gelezen:{MessageId}");
    }

    [Fact]
    public async Task BepaalVerwerkingId_Herhaal_VerhoogtPogingenEnHergebruiktRij()
    {
        var opslag = new OpnemendeOpslag { Stand = Stand(VerwerkingsBesluit.HerhaalVerwerking) };

        var id = await EmailVerwerkingsAfhandeling.BepaalVerwerkingIdAsync(
            MessageId, opslag, new OpnemendeMailbox(), NullLogger.Instance);

        id.Should().Be(42);
        opslag.Aanroepen.Should().Equal("verhoog:42");
    }

    [Fact]
    public async Task BepaalVerwerkingId_Opgeven_LegtFoutVastEnMarkeertGelezen()
    {
        var opslag = new OpnemendeOpslag { Stand = Stand(VerwerkingsBesluit.OpgevenNaMaxPogingen, pogingen: 3) };
        var mailbox = new OpnemendeMailbox();

        var id = await EmailVerwerkingsAfhandeling.BepaalVerwerkingIdAsync(MessageId, opslag, mailbox, NullLogger.Instance);

        id.Should().BeNull();
        opslag.Aanroepen.Should().Equal("fout:42:Opgegeven na 3 mislukte pogingen (verwerking) — handmatige opvolging nodig");
        mailbox.Aanroepen.Should().Equal($"gelezen:{MessageId}");
    }

    [Fact]
    public async Task BepaalVerwerkingId_OnbeslisteVerzendpoging_ZetOpReviewLabeltEnMarkeertGelezen()
    {
        var opslag = new OpnemendeOpslag { Stand = Stand(VerwerkingsBesluit.OnbeslistNaVerzendPoging) };
        var mailbox = new OpnemendeMailbox();

        var id = await EmailVerwerkingsAfhandeling.BepaalVerwerkingIdAsync(MessageId, opslag, mailbox, NullLogger.Instance);

        id.Should().BeNull();
        opslag.Aanroepen.Should().Equal("review:42");
        mailbox.Aanroepen.Should().Equal($"label:{MessageId}", $"gelezen:{MessageId}");
    }

    [Fact]
    public async Task OnbeslisteVerzendpoging_LabelFaalt_MarkeertTochGelezen()
    {
        var opslag = new OpnemendeOpslag { Stand = Stand(VerwerkingsBesluit.OnbeslistNaVerzendPoging) };
        var mailbox = new OpnemendeMailbox { LabelGooit = true };

        await EmailVerwerkingsAfhandeling.BepaalVerwerkingIdAsync(MessageId, opslag, mailbox, NullLogger.Instance);

        mailbox.Aanroepen.Should().Equal($"gelezen:{MessageId}");
    }

    [Fact]
    public async Task RegistreerClassificatieFout_NieuwBericht_RegistreertEnLegtFoutVast()
    {
        var opslag = new OpnemendeOpslag();
        var mailbox = new OpnemendeMailbox();

        await EmailVerwerkingsAfhandeling.RegistreerClassificatieFoutAsync(MessageId, opslag, mailbox, NullLogger.Instance);

        opslag.Aanroepen.Should().Equal("insert", "fout:77:AI-classificatie mislukt");
        mailbox.Aanroepen.Should().BeEmpty("het bericht blijft ongelezen voor de volgende poll");
    }

    [Fact]
    public async Task RegistreerClassificatieFout_Herhaal_VerhoogtEnLegtFoutVast()
    {
        var opslag = new OpnemendeOpslag { Stand = Stand(VerwerkingsBesluit.HerhaalVerwerking) };

        await EmailVerwerkingsAfhandeling.RegistreerClassificatieFoutAsync(
            MessageId, opslag, new OpnemendeMailbox(), NullLogger.Instance);

        opslag.Aanroepen.Should().Equal("verhoog:42", "fout:42:AI-classificatie mislukt");
    }

    [Fact]
    public async Task RegistreerClassificatieFout_Opgeven_GebruiktFaseAiClassificatie()
    {
        var opslag = new OpnemendeOpslag { Stand = Stand(VerwerkingsBesluit.OpgevenNaMaxPogingen, pogingen: 3) };
        var mailbox = new OpnemendeMailbox();

        await EmailVerwerkingsAfhandeling.RegistreerClassificatieFoutAsync(MessageId, opslag, mailbox, NullLogger.Instance);

        opslag.Aanroepen.Should().Equal("fout:42:Opgegeven na 3 mislukte pogingen (AI-classificatie) — handmatige opvolging nodig");
        mailbox.Aanroepen.Should().Equal($"gelezen:{MessageId}");
    }

    [Fact]
    public async Task RegistreerClassificatieFout_AlAfgerond_RaaktDeRijNietAan()
    {
        var opslag = new OpnemendeOpslag { Stand = Stand(VerwerkingsBesluit.OverslaanAlAfgerond) };
        var mailbox = new OpnemendeMailbox();

        await EmailVerwerkingsAfhandeling.RegistreerClassificatieFoutAsync(MessageId, opslag, mailbox, NullLogger.Instance);

        opslag.Aanroepen.Should().BeEmpty();
        mailbox.Aanroepen.Should().Equal($"gelezen:{MessageId}");
    }

    [Fact]
    public async Task RegistreerClassificatieFout_GelijktijdigeRegistratie_LegtGeenFoutVast()
    {
        var opslag = new OpnemendeOpslag { InsertGooitGelijktijdig = true };

        await EmailVerwerkingsAfhandeling.RegistreerClassificatieFoutAsync(
            MessageId, opslag, new OpnemendeMailbox(), NullLogger.Instance);

        opslag.Aanroepen.Should().Equal("insert");
    }

    [Fact]
    public async Task RegistreerClassificatieFout_OpslagFaalt_GooitNiet()
    {
        var opslag = new OpnemendeOpslag { HaalStandGooit = true };

        var act = () => EmailVerwerkingsAfhandeling.RegistreerClassificatieFoutAsync(
            MessageId, opslag, new OpnemendeMailbox(), NullLogger.Instance);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LegVerwerkingsFoutVast_BestaandeRij_LegtFoutVastOpHetId()
    {
        var opslag = new OpnemendeOpslag { Stand = Stand(VerwerkingsBesluit.HerhaalVerwerking) };

        await EmailVerwerkingsAfhandeling.LegVerwerkingsFoutVastAsync(MessageId, "Time-out", opslag, NullLogger.Instance);

        opslag.Aanroepen.Should().Equal("fout:42:Time-out");
    }

    [Fact]
    public async Task LegVerwerkingsFoutVast_GeenRij_LegtNietsVast()
    {
        var opslag = new OpnemendeOpslag();

        await EmailVerwerkingsAfhandeling.LegVerwerkingsFoutVastAsync(MessageId, "Time-out", opslag, NullLogger.Instance);

        opslag.Aanroepen.Should().BeEmpty();
    }

    [Fact]
    public async Task LegVerwerkingsFoutVast_OpslagFaalt_GooitNiet()
    {
        var opslag = new OpnemendeOpslag { HaalStandGooit = true };

        var act = () => EmailVerwerkingsAfhandeling.LegVerwerkingsFoutVastAsync(MessageId, "x", opslag, NullLogger.Instance);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandelBuitenScope_BinnenScope_DoetNiets()
    {
        var vastgelegd = false;
        var mailbox = new OpnemendeMailbox();

        var afgehandeld = await EmailVerwerkingsAfhandeling.HandelBuitenScopeAsync(
            42, MessageId, isBuitenScope: false, () => { vastgelegd = true; return Task.CompletedTask; },
            mailbox, NullLogger.Instance);

        afgehandeld.Should().BeFalse();
        vastgelegd.Should().BeFalse();
        mailbox.Aanroepen.Should().BeEmpty();
    }

    [Fact]
    public async Task HandelBuitenScope_BuitenScope_LegtVastLabeltEnMarkeertGelezen()
    {
        var vastgelegd = false;
        var mailbox = new OpnemendeMailbox();

        var afgehandeld = await EmailVerwerkingsAfhandeling.HandelBuitenScopeAsync(
            42, MessageId, isBuitenScope: true, () => { vastgelegd = true; return Task.CompletedTask; },
            mailbox, NullLogger.Instance);

        afgehandeld.Should().BeTrue();
        vastgelegd.Should().BeTrue();
        mailbox.Aanroepen.Should().Equal($"label:{MessageId}", $"gelezen:{MessageId}");
    }

    [Fact]
    public async Task HandelBuitenScope_LabelFaalt_IsTochAfgehandeld()
    {
        var mailbox = new OpnemendeMailbox { LabelGooit = true };

        var afgehandeld = await EmailVerwerkingsAfhandeling.HandelBuitenScopeAsync(
            42, MessageId, isBuitenScope: true, () => Task.CompletedTask, mailbox, NullLogger.Instance);

        afgehandeld.Should().BeTrue();
        mailbox.Aanroepen.Should().BeEmpty("labelen en gelezen markeren zitten in één poging, zoals vóór #1650");
    }
}
