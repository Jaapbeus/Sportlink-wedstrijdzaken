using FluentAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

public class EmailVerzendFoutClassificatieTests
{
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(413)]
    [InlineData(422)]
    [InlineData(429)]
    public void ExpliciteAfwijzingStatusCode_GeeftExplicieteAfwijzing(int statusCode)
    {
        var uitkomst = EmailVerzendFoutClassificatie.Classificeer(new InvalidOperationException("simulatie"), statusCode);

        uitkomst.Should().Be(EmailVerzendUitkomst.ExplicieteAfwijzing);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(408)]
    public void ServerFoutOfTimeoutStatusCode_GeeftOnbekendeUitkomst(int statusCode)
    {
        var uitkomst = EmailVerzendFoutClassificatie.Classificeer(new InvalidOperationException("simulatie"), statusCode);

        uitkomst.Should().Be(EmailVerzendUitkomst.OnbekendeUitkomst);
    }

    [Fact]
    public void OntbrekendeStatusCode_GeeftOnbekendeUitkomst()
    {
        var uitkomst = EmailVerzendFoutClassificatie.Classificeer(new InvalidOperationException("simulatie"), null);

        uitkomst.Should().Be(EmailVerzendUitkomst.OnbekendeUitkomst);
    }

    [Fact]
    public void TaskCanceledException_GeeftAltijdOnbekendeUitkomst_OokMetExplicieteStatusCode()
    {
        // Zelfs als er toevallig een 400 wordt meegegeven: een time-out/annulering betekent dat er
        // geen (volledige) respons is ontvangen, dus mag nooit als bewezen afwijzing gelden.
        var uitkomst = EmailVerzendFoutClassificatie.Classificeer(new TaskCanceledException(), 400);

        uitkomst.Should().Be(EmailVerzendUitkomst.OnbekendeUitkomst);
    }

    [Fact]
    public void OperationCanceledException_GeeftOnbekendeUitkomst()
    {
        var uitkomst = EmailVerzendFoutClassificatie.Classificeer(new OperationCanceledException(), null);

        uitkomst.Should().Be(EmailVerzendUitkomst.OnbekendeUitkomst);
    }

    [Fact]
    public void HttpRequestException_GeeftOnbekendeUitkomst()
    {
        var uitkomst = EmailVerzendFoutClassificatie.Classificeer(new HttpRequestException("connection lost"), null);

        uitkomst.Should().Be(EmailVerzendUitkomst.OnbekendeUitkomst);
    }

    [Fact]
    public void IOException_GeeftOnbekendeUitkomst()
    {
        var uitkomst = EmailVerzendFoutClassificatie.Classificeer(new IOException("stream afgebroken"), null);

        uitkomst.Should().Be(EmailVerzendUitkomst.OnbekendeUitkomst);
    }

    [Fact]
    public void SocketException_GeeftOnbekendeUitkomst()
    {
        var uitkomst = EmailVerzendFoutClassificatie.Classificeer(
            new System.Net.Sockets.SocketException(), null);

        uitkomst.Should().Be(EmailVerzendUitkomst.OnbekendeUitkomst);
    }

    [Fact]
    public void OnbekendeStatusCodeBuitenLijst_GeeftOnbekendeUitkomst()
    {
        // 418 komt niet voor in de expliciete-afwijzingslijst — een niet-herkende statuscode moet
        // fail-safe naar "onbekend" gaan, niet fail-open naar "afgewezen".
        var uitkomst = EmailVerzendFoutClassificatie.Classificeer(new InvalidOperationException("simulatie"), 418);

        uitkomst.Should().Be(EmailVerzendUitkomst.OnbekendeUitkomst);
    }
}
