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

    [Fact]
    public void VoegToestemmingenToe_ScheidsrechterNietToegestaan_NultDeDrieRelatieCodeVelden()
    {
        // #1340: mag een rol geen scheidsrechter toewijzen, dan mag hij ook de huidige relatiecode
        // niet zien — anders lekt de mutatie-blokkade alsnog via de GET-respons.
        var match = JsonSerializer.Deserialize<SportlinkMatch>(
            """
            {
                "publicMatchId": "M000000001",
                "externalMatchId": "1",
                "matchDate": "2026-09-15T19:30:00+02:00",
                "matchStatus": "CONCEPT",
                "isHomeMatch": true,
                "isCanceledMatch": false,
                "isConceptMatch": true,
                "isEditFieldAllowed": true,
                "isAssignDressingRoomsAllowed": true,
                "isAssignOfficialsAllowed": true,
                "isEditFieldSidePanelAllowed": true,
                "isAddScoreAllowed": true,
                "matchOfficials": [
                    { "OfficialPosition": "Referee", "RelatieCode": "111111" },
                    { "OfficialPosition": "AssistantReferee1", "RelatieCode": "222222" },
                    { "OfficialPosition": "AssistantReferee2", "RelatieCode": "333333" }
                ]
            }
            """)!;
        var toestemmingen = new SportlinkRolFeatureToestemmingen(Kleedkamers: true, Scheidsrechter: false, Veld: true);

        var payload = SportlinkRolFeature.VoegToestemmingenToe(match, toestemmingen);

        payload["scheidsrechterRelatieCode"].Should().BeNull();
        payload["ar1RelatieCode"].Should().BeNull();
        payload["ar2RelatieCode"].Should().BeNull();
    }

    [Fact]
    public void VoegToestemmingenToe_ScheidsrechterToegestaan_LaatDeDrieRelatieCodeVeldenStaan()
    {
        var match = JsonSerializer.Deserialize<SportlinkMatch>(
            """
            {
                "publicMatchId": "M000000001",
                "externalMatchId": "1",
                "matchDate": "2026-09-15T19:30:00+02:00",
                "matchStatus": "CONCEPT",
                "isHomeMatch": true,
                "isCanceledMatch": false,
                "isConceptMatch": true,
                "isEditFieldAllowed": true,
                "isAssignDressingRoomsAllowed": true,
                "isAssignOfficialsAllowed": true,
                "isEditFieldSidePanelAllowed": true,
                "isAddScoreAllowed": true,
                "matchOfficials": [
                    { "OfficialPosition": "Referee", "RelatieCode": "111111" }
                ]
            }
            """)!;
        var toestemmingen = new SportlinkRolFeatureToestemmingen(Kleedkamers: true, Scheidsrechter: true, Veld: true);

        var payload = SportlinkRolFeature.VoegToestemmingenToe(match, toestemmingen);

        payload["scheidsrechterRelatieCode"]!.GetValue<string>().Should().Be("111111");
        payload["ar1RelatieCode"].Should().BeNull();
    }
}
