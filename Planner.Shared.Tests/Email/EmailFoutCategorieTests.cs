using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>Privacy-veilige foutherkenning van de e-mailverwerker (#425, gedeeld sinds #1650).</summary>
public class EmailFoutCategorieTests
{
    [Theory]
    [InlineData("Error: insufficient_quota", true)]
    [InlineData("INSUFFICIENT_QUOTA", true)]
    [InlineData("HTTP 429 Too Many Requests", true)]
    [InlineData("HTTP 500", false)]
    public void IsOpenAiQuotaFout_HerkentQuotaEn429(string melding, bool verwacht)
        => EmailFoutCategorie.IsOpenAiQuotaFout(new InvalidOperationException(melding)).Should().Be(verwacht);

    [Fact]
    public void IsOpenAiQuotaFout_KijktOokNaarDeInnerException()
        => EmailFoutCategorie.IsOpenAiQuotaFout(
                new InvalidOperationException("buitenste", new InvalidOperationException("insufficient_quota")))
            .Should().BeTrue();

    [Theory]
    [InlineData("insufficient_quota", "OpenAI quota overschreden")]
    [InlineData("Login failed for user", "Database niet beschikbaar")]
    [InlineData("A connection was refused", "Database niet beschikbaar")]
    [InlineData("ResourceNotFound", "Graph API: bericht niet gevonden")]
    [InlineData("403 Forbidden", "Graph API: autorisatiefout")]
    [InlineData("The operation timed out", "Time-out bij externe service")]
    [InlineData("iets anders", "Onverwachte verwerkingsfout")]
    public void Categoriseer_GeeftVasteOmschrijving(string melding, string verwacht)
        => EmailFoutCategorie.Categoriseer(new InvalidOperationException(melding)).Should().Be(verwacht);

    [Fact]
    public void Categoriseer_NeemtNooitDeRuweMeldingOver()
    {
        var omschrijving = EmailFoutCategorie.Categoriseer(
            new InvalidOperationException("fout voor trainer@voorbeeld.nl"));

        omschrijving.Should().NotContain("trainer@voorbeeld.nl");
    }
}
