using AwesomeAssertions;
using SportlinkFunction.Email;
using Planner.Shared.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Karakterisatietests voor de pure kern van fase 1 van <c>EmailProcessorFunction</c> (#1629):
/// welke berichten na een mislukte AI-classificatie alsnog in de pogingenteller terechtkomen.
/// </summary>
public class EmailProcessorFasenTests
{
    private static InkomendBericht Bericht(string id) => new() { MessageId = id, Afzender = "trainer@voorbeeld.nl", Onderwerp = "Test" };

    private static List<(InkomendBericht Email, BerichtClassificatie Classificatie)> Geclassificeerd(params InkomendBericht[] berichten)
        => berichten.Select(b => (b, new BerichtClassificatie())).ToList();

    [Fact]
    public void BepaalMislukteClassificaties_BerichtZonderClassificatie_Telt()
    {
        var a = Bericht("a");
        var b = Bericht("b");

        var mislukt = EmailProcessorFunction.BepaalMislukteClassificaties([a, b], Geclassificeerd(a), aiAborted: false);

        mislukt.Should().ContainSingle().Which.MessageId.Should().Be("b");
    }

    [Fact]
    public void BepaalMislukteClassificaties_AfgebrokenBatch_TeltNiemandMee()
    {
        var a = Bericht("a");
        var b = Bericht("b");

        var mislukt = EmailProcessorFunction.BepaalMislukteClassificaties([a, b], Geclassificeerd(a), aiAborted: true);

        mislukt.Should().BeEmpty();
    }

    [Fact]
    public void BepaalMislukteClassificaties_AllesGeclassificeerd_IsLeeg()
    {
        var a = Bericht("a");

        EmailProcessorFunction.BepaalMislukteClassificaties([a], Geclassificeerd(a), aiAborted: false).Should().BeEmpty();
    }
}
