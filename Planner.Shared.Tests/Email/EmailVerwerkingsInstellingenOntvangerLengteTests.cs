using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public sealed class EmailVerwerkingsInstellingenOntvangerLengteTests
{
    [Theory]
    [InlineData(319, true)]
    [InlineData(320, true)]
    [InlineData(321, false)]
    public void TryNormaliseerOntvanger_HoudtZichAanKolomlengteNaTrimmen(int lengte, bool verwachtGeldig)
    {
        var invoer = KaalAdres(lengte);

        var geldig = EmailVerwerkingsInstellingenValidatie.TryNormaliseerOntvanger(invoer, out var ontvanger);

        geldig.Should().Be(verwachtGeldig);
        ontvanger.Should().Be(verwachtGeldig ? invoer : null);
    }

    [Fact]
    public void TryNormaliseerOntvanger_TrimtVoorLengtecontroleEnBehoudtLegeWaarden()
    {
        var invoer = $"  {KaalAdres(320)}  ";

        EmailVerwerkingsInstellingenValidatie.TryNormaliseerOntvanger(invoer, out var ontvanger).Should().BeTrue();
        ontvanger.Should().Be(KaalAdres(320));

        EmailVerwerkingsInstellingenValidatie.TryNormaliseerOntvanger("  \t ", out ontvanger).Should().BeTrue();
        ontvanger.Should().BeNull();
    }

    [Fact]
    public void AdminVoorbereiding_WeigertReviewOntvangerBovenKolomlengte()
    {
        var huidig = EmailVerwerkingsInstellingenLezer.LeesVeilig(
            new(false, true, null, true, false), databaseLezingGelukt: true);
        var wijzigingen = new Dictionary<string, string?>
        {
            ["EmailReviewOntvanger"] = KaalAdres(321)
        };

        var act = () => EmailInstellingenAdminCore.BereidVoor(huidig, wijzigingen, bevestigd: false);

        act.Should().Throw<EmailVerwerkingsInstellingenWijzigingException>()
            .Which.Fout.Should().Be(EmailVerwerkingsInstellingenValidatieFout.OntvangerOngeldig);
    }

    private static string KaalAdres(int lengte)
    {
        const string domein = "@voorbeeld.nl";
        return new string('a', lengte - domein.Length) + domein;
    }
}
