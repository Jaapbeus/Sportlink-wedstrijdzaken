using AwesomeAssertions;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>Foutmeldingen uit API-responsen tonen de <c>error</c>-tekst, nooit ruwe JSON (#1529).</summary>
public class AdminApiClientFoutTekstTests
{
    [Fact]
    public void JsonMetError_GeeftAlleenDeMelding()
    {
        var tekst = AdminApiClient.FoutTekst(503, "{\"error\":\"Nu niet beschikbaar.\",\"aiBeschikbaar\":false}");
        tekst.Should().Be("Nu niet beschikbaar.");
    }

    [Theory]
    [InlineData("<html>bad gateway</html>")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("{\"andere\":1}")]
    public void GeenBruikbareError_GeeftNeutraleTekstMetStatus(string? body)
    {
        var tekst = AdminApiClient.FoutTekst(502, body);
        tekst.Should().Contain("502").And.NotContain("{").And.NotContain("<html>");
    }

    [Theory]
    [InlineData("{\"code\":\"dubbelzinnig\",\"error\":\"x\"}", "dubbelzinnig")]
    [InlineData("{\"error\":\"x\"}", null)]
    [InlineData("{\"code\":5}", null)]
    [InlineData("<html>", null)]
    [InlineData(null, null)]
    public void FoutCode_LeestHetCodeVeldUitDeRespons(string? body, string? verwacht)
        => AdminApiClient.FoutCode(body).Should().Be(verwacht);
}
