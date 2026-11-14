using AwesomeAssertions;
using SportlinkFunction.Email;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

public sealed class EmailBerichtPoortTests
{
    [Fact]
    public async Task BuitenScope_GebruiktBestaandeTierKoppelingVoorStatusEnMailbox()
    {
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();
        var poort = new EmailBerichtPoort(graph, persistence, null!, null!, null!);
        var resultaat = await poort.HandelBuitenScopeAsync(12, new() { MessageId = "synthetic-message-id" },
            new() { Type = VerzoekType.BuitenScope }, "{\"Type\":4}", NullLogger.Instance);

        resultaat.Should().BeTrue();
        persistence.StatusUpdates.Should().ContainSingle(u => u.VerwerkingId == 12
            && u.Status == EmailStatus.BuitenScope && u.GeextraheerdeData == "{\"Type\":4}");
        graph.MarkedAsReadIds.Should().Equal("synthetic-message-id");
        graph.SentReplies.Should().BeEmpty();
    }

    [Fact]
    public void Body_BewaartNietNullBodyOngewijzigd()
    {
        EmailBerichtPoort.Body(new InkomendBericht { Body = "berichtinhoud" }).Should().Be("berichtinhoud");
    }
}
