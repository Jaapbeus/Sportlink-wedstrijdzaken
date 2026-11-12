using System.Text.Json;
using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

/// <summary>
/// #1871: de <c>ClubMatch</c>-body zet thuis- en uit-ID apart. Live bevestigd (2026-11-11) met Sportlinks eigen
/// formulier: eigen clubteam als <c>PublicHomeTeamId</c>, een vrije-tekst-tegenstander als <c>AwayTeam</c> met de
/// "geen team"-sentinel als <c>PublicAwayTeamId</c>. Alle waarden fictief.
/// </summary>
public class ClubMatchUitteamBodyTests
{
    private static SportlinkClubMatchAanvraag Aanvraag(string? uitId = null)
    {
        var a = new SportlinkClubMatchAanvraag(
            MatchDate: new DateOnly(2026, 10, 4), StartTime: new TimeOnly(12, 0), Duration: 90, ExternalMatchId: 26100401,
            Description: "Oefenwedstrijd", HomeTeam: "35+4", AwayTeam: "TEST", PublicTeamId: "T-EIGEN",
            AgeClassCode: "001", SportIdTag: "SOCCER-VE-AL/FRIDAY", IsHomeMatch: true, FacilityId: "F1", SubFacilityId: "F1-1");
        return uitId == null ? a : a with { PublicAwayTeamId = uitId };
    }

    private static JsonElement Body(SportlinkClubMatchAanvraag a)
        => JsonDocument.Parse(JsonSerializer.Serialize(SportlinkMutatieBodies.BuildClubMatchBody(a))).RootElement;

    [Fact]
    public void BuildClubMatchBody_ExterneTegenstander_ThuisEigenTeamUitSentinel()
    {
        var body = Body(Aanvraag());

        body.GetProperty("PublicHomeTeamId").GetString().Should().Be("T-EIGEN");
        body.GetProperty("PublicAwayTeamId").GetString().Should().Be(SportlinkClubMatchAanvraag.GeenTeamId,
            "vóór #1871 ging hier het eigen team-ID mee en toonde Sportlink het eigen team als uitteam");
        body.GetProperty("HomeTeam").GetString().Should().Be("35+4");
        body.GetProperty("AwayTeam").GetString().Should().Be("TEST");
    }

    [Fact]
    public void BuildClubMatchBody_ExplicietUitId_GaatOngewijzigdMee()
    {
        Body(Aanvraag("T-EXTERN")).GetProperty("PublicAwayTeamId").GetString().Should().Be("T-EXTERN");
    }

    [Fact]
    public void BouwPlusBody_ClubteamTegenTegenstander_TweeVerschillendeIds()
    {
        // De keten zoals het endpoint hem doorloopt: formulierinvoer → aanvraag → JSON-body.
        var context = new SportlinkClubMatchContext(
            new SportlinkClubMatchDefaults(41, "Oefenwedstrijd", SportlinkClubMatchAanvraag.GeenTeamId, "SOCCER-VE-AL/FRIDAY", true,
                "F1", "F1-1", "1.0", "0", "001", PublicAwayTeamId: SportlinkClubMatchAanvraag.GeenTeamId),
            new[] { new SportlinkClubTeam("T354", "35+4", "35+4 - Mannen", "SOCCER-VE-AL", "FRIDAY") },
            new[] { new SportlinkClubFacility("F1", "Sportpark Oost", IsDefault: true, new[] { new SportlinkClubField("F1-1", "veld 1", "veld 1") }) },
            new[] { new SportlinkClubActivity("SOCCER-VE-AL/FRIDAY", "Veld - Vrijdag") },
            new[] { new SportlinkClubAgeClass("001", "Senioren (M)") });
        var invoer = new ClubMatchInvoer(new DateTime(2026, 10, 4, 12, 0, 0), 90, "AllStars 35+4", false, "1-99", "TEST", "veld 1",
            "Oefenwedstrijd", "Sportpark Oost");

        var body = Body(ClubMatchAanvraagBouwer.Bouw(invoer, context).Aanvraag!);

        body.GetProperty("PublicHomeTeamId").GetString().Should().Be("T354");
        body.GetProperty("PublicAwayTeamId").GetString().Should().Be(SportlinkClubMatchAanvraag.GeenTeamId);
        body.GetProperty("AwayTeam").GetString().Should().Be("TEST");
    }

    [Fact]
    public void ClubMatchDefaults_LiveVorm_LeestPublicAwayTeamId()
    {
        // Live vorm (2026-11-11): ClubMatchDefaults draagt HomeTeam/PublicHomeTeamId/AwayTeam/PublicAwayTeamId.
        const string json = """{"ExternalMatchId":7,"Description":"Oefenwedstrijd","HomeTeam":"","PublicHomeTeamId":"T2010269033","AwayTeam":"","PublicAwayTeamId":"T2010269033","SportIdTag":"SOCCER-VE-AL/FRIDAY","IsHomeMatch":true,"FacilityId":"F1","SubFacilityId":"F1-1","FieldSize":"1.0","FieldOffset":0,"AgeClassCode":"001"}""";

        var d = JsonSerializer.Deserialize<SportlinkClubMatchDefaults>(json, SportlinkClubHttp.JsonOptions)!;

        d.PublicAwayTeamId.Should().Be(SportlinkClubMatchAanvraag.GeenTeamId);
        d.PublicHomeTeamId.Should().Be(SportlinkClubMatchAanvraag.GeenTeamId);
    }
}
