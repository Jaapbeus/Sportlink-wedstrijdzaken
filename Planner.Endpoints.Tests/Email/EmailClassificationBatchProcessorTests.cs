using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Endpoints.Tests.Email;

public class EmailClassificationBatchProcessorTests
{
    private sealed record Bericht(string MessageId, string Afzender, string Onderwerp) : IInkomendBericht
    {
        public string AfzenderNaam => "";
    }

    [Fact]
    public async Task ClassificeerBatchAsync_LegeBatch_GeeftLegeNietAfgebrokenUitkomst()
    {
        var calls = 0;
        var result = await EmailClassificationBatchProcessor.ClassificeerBatchAsync(
            Array.Empty<Bericht>(),
            (_, _) => { calls++; return Task.FromResult(new BerichtClassificatie()); },
            _ => false,
            _ => "",
            email => email.Onderwerp,
            (_, _, _) => { },
            NullLogger.Instance);

        result.Classificaties.Should().BeEmpty();
        result.AiAborted.Should().BeFalse();
        result.QuotaException.Should().BeNull();
        result.NietQuotaClassificatieFouten.Should().Be(0);
        calls.Should().Be(0);
    }

    [Fact]
    public async Task ClassificeerBatchAsync_NietQuotaFout_GaatDoorEnValideertDatumMetTiervelden()
    {
        var emails = new[] { new Bericht("m1", "a@allstars-fc.test", "A"), new Bericht("m2", "b@allstars-fc.test", "B") };
        var calls = 0;
        var validaties = new List<(BerichtClassificatie Classificatie, string Body, string Onderwerp)>();

        var result = await EmailClassificationBatchProcessor.ClassificeerBatchAsync(
            emails,
            (email, _) =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("classification failed");
                return Task.FromResult(new BerichtClassificatie { Type = VerzoekType.Bevestiging });
            },
            _ => false,
            email => email.MessageId == "m1" ? "first body" : "second body",
            email => email.Onderwerp,
            (classificatie, body, onderwerp) => validaties.Add((classificatie, body, onderwerp)),
            NullLogger.Instance);

        result.AiAborted.Should().BeFalse();
        result.NietQuotaClassificatieFouten.Should().Be(1);
        result.Classificaties.Select(item => item.Email.MessageId).Should().Equal("m2");
        validaties.Should().ContainSingle().Which.Should().Match<(BerichtClassificatie Classificatie, string Body, string Onderwerp)>(
            item => item.Body == "second body" && item.Onderwerp == "B");
    }

    [Fact]
    public async Task ClassificeerBatchAsync_QuotaFout_HoudtGedeeltelijkResultaatEnStoptDirect()
    {
        var emails = new[] { new Bericht("m1", "a@allstars-fc.test", "A"), new Bericht("m2", "b@allstars-fc.test", "B"), new Bericht("m3", "c@allstars-fc.test", "C") };
        var calls = 0;
        var quota = new InvalidOperationException("quota");

        var result = await EmailClassificationBatchProcessor.ClassificeerBatchAsync(
            emails,
            (email, _) =>
            {
                calls++;
                return email.MessageId == "m2"
                    ? Task.FromException<BerichtClassificatie>(quota)
                    : Task.FromResult(new BerichtClassificatie { Type = VerzoekType.Bevestiging });
            },
            ex => ReferenceEquals(ex, quota),
            _ => "body",
            email => email.Onderwerp,
            (_, _, _) => { },
            NullLogger.Instance);

        result.AiAborted.Should().BeTrue();
        result.QuotaException.Should().BeSameAs(quota);
        result.NietQuotaClassificatieFouten.Should().Be(0);
        result.Classificaties.Select(item => item.Email.MessageId).Should().Equal("m1");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task ClassificeerBatchAsync_ValidatiefoutTeltAlsBerichtfout_EnFewShotsWordenDoorgestuurd()
    {
        var voorbeeld = new ClassificatieCorrectieVoorbeeld("A", "B", "onderwerp", "tekst");
        IReadOnlyList<ClassificatieCorrectieVoorbeeld> voorbeelden = [voorbeeld];
        IReadOnlyList<ClassificatieCorrectieVoorbeeld>? ontvangenVoorbeelden = null;
        var validatieException = new InvalidOperationException("invalid result");

        var result = await EmailClassificationBatchProcessor.ClassificeerBatchAsync(
            [new Bericht("m1", "a@allstars-fc.test", "A")],
            (_, fewShots) =>
            {
                ontvangenVoorbeelden = fewShots;
                return Task.FromResult(new BerichtClassificatie { Type = VerzoekType.Bevestiging });
            },
            _ => false,
            _ => "body",
            email => email.Onderwerp,
            (_, _, _) => throw validatieException,
            NullLogger.Instance,
            voorbeelden);

        result.AiAborted.Should().BeFalse();
        result.Classificaties.Should().BeEmpty();
        result.NietQuotaClassificatieFouten.Should().Be(1);
        ontvangenVoorbeelden.Should().BeSameAs(voorbeelden);
    }
}
