using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

public class EmailClassificationServiceTests
{
    [Fact]
    public async Task ClassificeerBatchAsync_ValideertDagDatumOpResultaat()
    {
        var service = new EmailClassificationService();
        var emails = new List<InkomendBericht>
        {
            new()
            {
                MessageId = "m1",
                Onderwerp = "Beschikbaarheid 18-4-2026",
                Body = "Kunnen jullie dan?",
                Afzender = "x@club.nl"
            }
        };

        var result = await service.ClassificeerBatchAsync(
            emails,
            _ => Task.FromResult(new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck, Datum = null }),
            _ => false,
            NullLogger.Instance);

        result.AiAborted.Should().BeFalse();
        result.Classificaties.Should().ContainSingle();
        result.Classificaties[0].Classificatie.Datum.Should().Be("2026-04-18");
    }

    [Fact]
    public async Task ClassificeerBatchAsync_NietQuotaFout_GaatDoorMetVolgendeEmail()
    {
        var service = new EmailClassificationService();
        var emails = new List<InkomendBericht>
        {
            new() { MessageId = "m1", Onderwerp = "A", Body = "A", Afzender = "a@x.nl" },
            new() { MessageId = "m2", Onderwerp = "B", Body = "B", Afzender = "b@x.nl" }
        };
        var calls = 0;

        var result = await service.ClassificeerBatchAsync(
            emails,
            _ =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("boom");
                return Task.FromResult(new BerichtClassificatie { Type = VerzoekType.Bevestiging });
            },
            _ => false,
            NullLogger.Instance);

        result.AiAborted.Should().BeFalse();
        result.QuotaException.Should().BeNull();
        result.Classificaties.Should().ContainSingle(c => c.Email.MessageId == "m2");
    }

    [Fact]
    public async Task ClassificeerBatchAsync_QuotaFout_BreektBatchAf()
    {
        var service = new EmailClassificationService();
        var emails = new List<InkomendBericht>
        {
            new() { MessageId = "m1", Onderwerp = "A", Body = "A", Afzender = "a@x.nl" },
            new() { MessageId = "m2", Onderwerp = "B", Body = "B", Afzender = "b@x.nl" },
            new() { MessageId = "m3", Onderwerp = "C", Body = "C", Afzender = "c@x.nl" }
        };
        var calls = 0;

        var result = await service.ClassificeerBatchAsync(
            emails,
            _ =>
            {
                calls++;
                if (calls == 2) throw new InvalidOperationException("HTTP 429 insufficient_quota");
                return Task.FromResult(new BerichtClassificatie { Type = VerzoekType.Bevestiging });
            },
            ex => ex.Message.Contains("429", StringComparison.Ordinal),
            NullLogger.Instance);

        result.AiAborted.Should().BeTrue();
        result.QuotaException.Should().NotBeNull();
        result.Classificaties.Should().ContainSingle(c => c.Email.MessageId == "m1");
        calls.Should().Be(2);
    }
}
