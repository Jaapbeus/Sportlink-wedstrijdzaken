using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>Legt de uit beide BerichtPipeline-bestanden verhuisde heuristieken vast (#1568).</summary>
public class BerichtTekstHeuristiekTests
{
    [Theory]
    [InlineData("Re: Oefenwedstrijd", true)]
    [InlineData("  FW: RE: iets", true)]
    [InlineData("AW: iets", true)]
    [InlineData("Oefenwedstrijd", false)]
    [InlineData("Wij re: spelen", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HeeftReplyPrefix_HerkentVoorvoegsel(string? onderwerp, bool verwacht)
        => BerichtTekstHeuristiek.HeeftReplyPrefix(onderwerp).Should().Be(verwacht);

    [Theory]
    [InlineData("Kunnen we vervroegen?", "", "vervroegen")]
    [InlineData("", "liever later spelen", "verlaten")]
    [InlineData("eerder of later", "", null)]
    [InlineData("niets bijzonders", null, null)]
    public void DetecteerRichting_AlleenBijEenduidigeRichting(string? onderwerp, string? body, string? verwacht)
        => BerichtTekstHeuristiek.DetecteerRichting(onderwerp, body).Should().Be(verwacht);

    [Fact]
    public void EerstvolgendVoorkomen_OngeldigeDatum_GeeftNull()
        => BerichtTekstHeuristiek.EerstvolgendVoorkomen(31, 2).Should().BeNull();

    [Fact]
    public void EerstvolgendVoorkomen_GeldigeDatum_LigtNietMeerDan30DagenInHetVerleden()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        var uitkomst = BerichtTekstHeuristiek.EerstvolgendVoorkomen(vandaag.Day > 28 ? 28 : vandaag.Day, vandaag.Month);
        uitkomst.Should().NotBeNull();
        uitkomst!.Value.Should().BeOnOrAfter(vandaag.AddDays(-30));
    }
}
