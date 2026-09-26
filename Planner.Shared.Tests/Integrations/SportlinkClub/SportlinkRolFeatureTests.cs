using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using System.Text.Json;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

public class SportlinkRolFeatureTests
{
    [Theory]
    [InlineData(SportlinkMutationSoort.Kleedkamers, SportlinkRolFeature.Kleedkamers)]
    [InlineData(SportlinkMutationSoort.Officials, SportlinkRolFeature.Scheidsrechter)]
    [InlineData(SportlinkMutationSoort.Veld, SportlinkRolFeature.Veld)]
    public void VoorMutatieSoort_InstelbareSoort_GeeftVerwachteFeatureKey(SportlinkMutationSoort soort, string verwacht)
    {
        SportlinkRolFeature.VoorMutatieSoort(soort).Should().Be(verwacht);
    }

    [Theory]
    [InlineData(SportlinkMutationSoort.VeldSidePanel)]
    [InlineData(SportlinkMutationSoort.Uitslag)]
    [InlineData(SportlinkMutationSoort.DatumTijdAccommodatie)]
    public void VoorMutatieSoort_NietInstelbareSoort_GeeftNull(SportlinkMutationSoort soort)
    {
        // #1341: bewust géén per-rol-toggle voor deze mutatiesoorten.
        SportlinkRolFeature.VoorMutatieSoort(soort).Should().BeNull();
    }

    [Fact]
    public void VoegToestemmingenToe_VoegtDrieVlaggenToeZonderRauweVeldenTeVerliezen()
    {
        var match = new SportlinkMatch { PublicMatchId = "M000000001" };
        var toestemmingen = new SportlinkRolFeatureToestemmingen(Kleedkamers: true, Scheidsrechter: false, Veld: true);

        var payload = SportlinkRolFeature.VoegToestemmingenToe(match, toestemmingen);

        payload["publicMatchId"]!.GetValue<string>().Should().Be("M000000001");
        payload["kleedkamersFeatureToegestaan"]!.GetValue<bool>().Should().BeTrue();
        payload["scheidsrechterFeatureToegestaan"]!.GetValue<bool>().Should().BeFalse();
        payload["veldFeatureToegestaan"]!.GetValue<bool>().Should().BeTrue();
    }
}
