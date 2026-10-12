using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>Berichten van automatische afzenders worden herkend aan hun internetheaders (#1674).</summary>
public class AutomatischeAfzenderHerkenningTests
{
    private static bool Automatisch(string naam, string waarde)
        => AutomatischeAfzenderHerkenning.IsAutomatisch([new KeyValuePair<string, string>(naam, waarde)]);

    [Fact]
    public void ZonderHeaders_IsHetGeenAutomatischeAfzender()
    {
        AutomatischeAfzenderHerkenning.IsAutomatisch(null).Should().BeFalse();
        AutomatischeAfzenderHerkenning.IsAutomatisch([]).Should().BeFalse();
    }

    [Theory]
    [InlineData("Auto-Submitted", "auto-replied", true)]
    [InlineData("Auto-Submitted", "auto-generated", true)]
    [InlineData("auto-submitted", "AUTO-REPLIED; type=vacation", true)]
    [InlineData("Auto-Submitted", "no", false)]
    [InlineData("Auto-Submitted", "No", false)]
    [InlineData("Auto-Submitted", "", false)]
    public void AutoSubmitted_AllesBehalveNoIsAutomatisch(string naam, string waarde, bool verwacht)
        => Automatisch(naam, waarde).Should().Be(verwacht);

    [Theory]
    [InlineData("bulk", true)]
    [InlineData("auto_reply", true)]
    [InlineData("junk", true)]
    [InlineData("list", true)]
    [InlineData("LIST", true)]
    [InlineData(" bulk ", true)]
    [InlineData("first-class", false)]
    [InlineData("", false)]
    public void Precedence_OnderdrukkendeWaardenZijnAutomatisch(string waarde, bool verwacht)
        => Automatisch("Precedence", waarde).Should().Be(verwacht);

    [Theory]
    [InlineData("All", true)]
    [InlineData("OOF", true)]
    [InlineData("AutoReply", true)]
    [InlineData("DR, NDR, OOF", true)]
    [InlineData("DR, NDR, RN, NRN", false)]
    [InlineData("", false)]
    public void AutoResponseSuppress_AlleenWaardenDieAntwoordenOnderdrukkenTellen(string waarde, bool verwacht)
        => Automatisch("X-Auto-Response-Suppress", waarde).Should().Be(verwacht);

    [Fact]
    public void OnbekendeHeadersZijnGeenReden()
        => Automatisch("X-Mailer", "Outlook").Should().BeFalse();

    [Fact]
    public void EenAutomatischeHeaderTussenGewoneHeadersVoldoet()
    {
        var headers = new[]
        {
            new KeyValuePair<string, string>("Received", "from x"),
            new KeyValuePair<string, string>("Auto-Submitted", "auto-replied"),
            new KeyValuePair<string, string>("Subject", "Afwezig"),
        };

        AutomatischeAfzenderHerkenning.IsAutomatisch(headers).Should().BeTrue();
    }

    [Fact]
    public void UitgaandeHeaders_VragenOmGeenAutomatischeAntwoorden()
    {
        AutomatischeAfzenderHerkenning.UitgaandeHeaders.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("X-Auto-Response-Suppress", "All"));

        // Een bericht met onze eigen uitgaande header wordt zelf als automatisch herkend.
        AutomatischeAfzenderHerkenning.IsAutomatisch(AutomatischeAfzenderHerkenning.UitgaandeHeaders).Should().BeTrue();
    }
}
