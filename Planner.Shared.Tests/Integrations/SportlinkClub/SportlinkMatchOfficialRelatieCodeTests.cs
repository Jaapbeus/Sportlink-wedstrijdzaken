using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using System.Text.Json;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

/// <summary>
/// Tests voor #1340 (scheidsrechter/AR1/AR2-relatiecode prefillen). De JSON hieronder is
/// SYNTHETISCHE FIXTUREDATA — zelf geconstrueerd, GEEN echte Sportlink-respons. Het exacte
/// veldnaam voor de relatiecode (<see cref="SportlinkMatchOfficial.RelatieCodeJsonVeldnaam"/>,
/// hier hardcoded als "RelatieCode" om de fixture leesbaar te houden) is NOOIT live geverifieerd
/// tegen de Sportlink-API. Deze tests bewijzen dus uitsluitend dat <see cref="SportlinkMatch"/>
/// de aangenomen JSON-vorm correct leest — NIET dat die aanname klopt met de werkelijkheid. Zie de
/// TODO bij <see cref="SportlinkMatchOfficial.RelatieCode"/> voor de owner-verificatiestap.
/// </summary>
public class SportlinkMatchOfficialRelatieCodeTests
{
    private const string MinimaleMatchVelden =
        """
        "publicMatchId": "M000000001",
        "externalMatchId": "123456",
        "matchDate": "2026-09-15T19:30:00+02:00",
        "matchStatus": "CONCEPT",
        "isHomeMatch": true,
        "isCanceledMatch": false,
        "isConceptMatch": true,
        "taskStatus": null,
        "isEditFieldAllowed": true,
        "isAssignDressingRoomsAllowed": true,
        "isAssignOfficialsAllowed": true,
        "isEditFieldSidePanelAllowed": true,
        "isAddScoreAllowed": true
        """;

    [Fact]
    public void SportlinkMatch_MatchOfficialsMetDrieBekendePosities_LeestElkeRelatieCodeCorrect()
    {
        // Synthetische fixture — nooit een echte Sportlink-respons, zie klasse-comment. Bevat
        // uitsluitend de twee ONBEVESTIGDE aangenomen velden (OfficialPosition/RelatieCode).
        var json = $$"""
        {
            {{MinimaleMatchVelden}},
            "matchOfficials": [
                { "OfficialPosition": "Referee", "RelatieCode": "111111" },
                { "OfficialPosition": "AssistantReferee1", "RelatieCode": "222222" },
                { "OfficialPosition": "AssistantReferee2", "RelatieCode": "333333" }
            ]
        }
        """;

        var match = JsonSerializer.Deserialize<SportlinkMatch>(json);

        match.Should().NotBeNull();
        match!.ScheidsrechterRelatieCode.Should().Be("111111");
        match.Ar1RelatieCode.Should().Be("222222");
        match.Ar2RelatieCode.Should().Be("333333");
    }

    [Fact]
    public void SportlinkMatch_MatchOfficialsOntbreektInJson_GeeftNullVoorAlleDrieZonderException()
    {
        var json = $$"""
        {
            {{MinimaleMatchVelden}}
        }
        """;

        var match = JsonSerializer.Deserialize<SportlinkMatch>(json);

        match.Should().NotBeNull();
        match!.MatchOfficials.Should().BeNull();
        match.ScheidsrechterRelatieCode.Should().BeNull();
        match.Ar1RelatieCode.Should().BeNull();
        match.Ar2RelatieCode.Should().BeNull();
    }

    [Fact]
    public void SportlinkMatch_MatchOfficialsExpliciteJsonNull_GeeftNullVoorAlleDrieZonderException()
    {
        var json = $$"""
        {
            {{MinimaleMatchVelden}},
            "matchOfficials": null
        }
        """;

        var match = JsonSerializer.Deserialize<SportlinkMatch>(json);

        match.Should().NotBeNull();
        match!.MatchOfficials.Should().BeNull();
        match.ScheidsrechterRelatieCode.Should().BeNull();
        match.Ar1RelatieCode.Should().BeNull();
        match.Ar2RelatieCode.Should().BeNull();
    }

    [Fact]
    public void SportlinkMatch_MatchOfficialsLegeArray_GeeftNullVoorAlleDrie()
    {
        var json = $$"""
        {
            {{MinimaleMatchVelden}},
            "matchOfficials": []
        }
        """;

        var match = JsonSerializer.Deserialize<SportlinkMatch>(json);

        match.Should().NotBeNull();
        match!.MatchOfficials.Should().NotBeNull().And.BeEmpty();
        match.ScheidsrechterRelatieCode.Should().BeNull();
        match.Ar1RelatieCode.Should().BeNull();
        match.Ar2RelatieCode.Should().BeNull();
    }

    [Fact]
    public void SportlinkMatch_MatchOfficialsMetOnbekendePositieOfOntbrekendeRelatieCode_MaptNooitVerkeerd()
    {
        // Een positie die geen van de drie bekende waarden is (bijv. een vierde official-type)
        // mag nooit per ongeluk aan Scheidsrechter/AR1/AR2 gekoppeld worden. Een official zonder
        // RelatieCode (nog niet toegewezen bij Sportlink) geeft null voor die positie, niet een
        // uitzondering.
        var json = $$"""
        {
            {{MinimaleMatchVelden}},
            "matchOfficials": [
                { "OfficialPosition": "SomeOtherPosition", "RelatieCode": "999999" },
                { "OfficialPosition": "Referee", "RelatieCode": null }
            ]
        }
        """;

        var match = JsonSerializer.Deserialize<SportlinkMatch>(json);

        match.Should().NotBeNull();
        match!.ScheidsrechterRelatieCode.Should().BeNull();
        match.Ar1RelatieCode.Should().BeNull();
        match.Ar2RelatieCode.Should().BeNull();
    }

    [Fact]
    public void SportlinkMatch_GeenEnkeleAndereMatchOfficialsEigenschapWordtGemodelleerd()
    {
        // AVG-grens (#1340): naam/geboortedatum/foto-URL mogen wel in de rauwe JSON staan (zie het
        // incident van 2026-09-06, docs/SPORTLINK-WEB-EXTENSION.md §5), maar SportlinkMatchOfficial
        // modelleert uitsluitend OfficialPosition en RelatieCode. Deze test documenteert die grens
        // expliciet: extra velden in de fixture worden simpelweg genegeerd door System.Text.Json
        // (geen [JsonExtensionData]), niet ergens alsnog opgevangen.
        var json = $$"""
        {
            {{MinimaleMatchVelden}},
            "matchOfficials": [
                {
                    "OfficialPosition": "Referee",
                    "RelatieCode": "111111",
                    "Name": "Voorbeeldnaam",
                    "DateOfBirth": "1980-01-01",
                    "PhotoUrl": "https://voorbeeld.test/foto.jpg"
                }
            ]
        }
        """;

        var match = JsonSerializer.Deserialize<SportlinkMatch>(json);

        match.Should().NotBeNull();
        match!.ScheidsrechterRelatieCode.Should().Be("111111");
        typeof(SportlinkMatchOfficial).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(nameof(SportlinkMatchOfficial.OfficialPosition), nameof(SportlinkMatchOfficial.RelatieCode));
    }
}
