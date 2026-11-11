using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public class EmailBatchFilterPolicyTests
{
    [Fact]
    public async Task HandelEigenMailboxBericht_NoodmailBlijftOngelezenEnWordtAfgehandeld()
    {
        var mailbox = new FakeMailbox();

        var afgehandeld = await EmailBatchFilterPolicy.HandelEigenMailboxBerichtAsync(
            new Bericht("m1", "trainer@voorbeeld.nl", "URGENT: Database unavailable"),
            "trainer@voorbeeld.nl",
            mailbox,
            NullLogger.Instance);

        afgehandeld.Should().BeTrue();
        mailbox.GelezenIds.Should().BeEmpty();
    }

    [Fact]
    public async Task HandelEigenMailboxBericht_GewoneEigenMailWordtGelezenGemarkeerd()
    {
        var mailbox = new FakeMailbox();

        var afgehandeld = await EmailBatchFilterPolicy.HandelEigenMailboxBerichtAsync(
            new Bericht("m1", "trainer@voorbeeld.nl", ""),
            "trainer@voorbeeld.nl",
            mailbox,
            NullLogger.Instance);

        afgehandeld.Should().BeTrue();
        mailbox.GelezenIds.Should().ContainSingle().Which.Should().Be("m1");
    }

    [Fact]
    public async Task HandelEigenMailboxBericht_ExterneNoodmailWordtNietAfgehandeld()
    {
        var mailbox = new FakeMailbox();

        var afgehandeld = await EmailBatchFilterPolicy.HandelEigenMailboxBerichtAsync(
            new Bericht("m1", "trainer@allstars-fc.test", "URGENT: Spoofed subject"),
            "trainer@voorbeeld.nl",
            mailbox,
            NullLogger.Instance);

        afgehandeld.Should().BeFalse();
        mailbox.GelezenIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("URGENT: Database unavailable", true)]
    [InlineData("urgent: quota reached", true)]
    [InlineData(" URGENT: leading whitespace", false)]
    [InlineData("URGENT notice", false)]
    [InlineData("", false)]
    public void IsNoodmailOnderwerp_MatchtAlleenVastVoorvoegsel(string? onderwerp, bool verwacht)
        => EmailBatchFilterPolicy.IsNoodmailOnderwerp(onderwerp).Should().Be(verwacht);

    [Fact]
    public void IsNoodmailOnderwerp_NullIsGeenNoodmail()
        => EmailBatchFilterPolicy.IsNoodmailOnderwerp(null).Should().BeFalse();

    private sealed record Bericht(string MessageId, string Afzender, string Onderwerp) : IInkomendBericht
    {
        public string AfzenderNaam => "";
    }

    private sealed class FakeMailbox : IEmailMailboxActies
    {
        public List<string> GelezenIds { get; } = [];

        public Task MarkeerGelezenAsync(string messageId)
        {
            GelezenIds.Add(messageId);
            return Task.CompletedTask;
        }

        public Task LabelGeenAiAntwoordAsync(string messageId) => Task.CompletedTask;
    }
}
