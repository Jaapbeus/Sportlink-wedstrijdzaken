using FluentAssertions;
using FunctionApp.Postgres.Integrations.SportlinkClub;
using FunctionApp.Tests.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt <see cref="SportlinkClubClient"/>'s JSON-parsing vast tegen een lokale fixture-server
/// (zelfde <see cref="SportlinkFixtureServer"/> als de SQL Server-tier's Sportlink-syncklasse
/// gebruikt, #867) — geen enkele echte netwerkaanroep naar club.sportlink.com. Beide getest
/// endpoints hebben géén bevestigde vaste respons-vorm (zie
/// docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md #991: "respons-vorm niet 100% bevestigd"), dus
/// dekt dit expliciet beide mogelijke vormen voor MatchProgramOverview.
/// </summary>
public class SportlinkClubClientTests
{
    private static HttpClient MaakClient(SportlinkFixtureServer server)
        => new() { BaseAddress = new Uri(server.BaseUrl + "/") };

    [Fact]
    public async Task ResolvePublicMatchIdAsync_RespondeertMetKaleArray_VindtDeGezochteWedstrijd()
    {
        using var server = new SportlinkFixtureServer();
        server.RespondWithJson("/competition/match/MatchProgramOverview", """
            [
              { "ExternalMatchId": 1111, "PublicMatchId": "M100000001" },
              { "ExternalMatchId": 3403, "PublicMatchId": "M392686417" }
            ]
            """);
        using var http = MaakClient(server);

        var result = await SportlinkClubClient.ResolvePublicMatchIdAsync(
            http, wedstrijdnummer: 3403, datum: new DateOnly(2026, 9, 5), accessToken: "test-token", NullLogger.Instance);

        result.Should().Be("M392686417");
    }

    [Fact]
    public async Task ResolvePublicMatchIdAsync_RespondeertGenestOnderMatchesProperty_VindtDeGezochteWedstrijd()
    {
        using var server = new SportlinkFixtureServer();
        server.RespondWithJson("/competition/match/MatchProgramOverview", """
            { "Matches": [ { "ExternalMatchId": 3403, "PublicMatchId": "M392686417" } ] }
            """);
        using var http = MaakClient(server);

        var result = await SportlinkClubClient.ResolvePublicMatchIdAsync(
            http, wedstrijdnummer: 3403, datum: new DateOnly(2026, 9, 5), accessToken: "test-token", NullLogger.Instance);

        result.Should().Be("M392686417");
    }

    [Fact]
    public async Task ResolvePublicMatchIdAsync_WedstrijdNietInRespons_GeeftNullTerug()
    {
        using var server = new SportlinkFixtureServer();
        server.RespondWithJson("/competition/match/MatchProgramOverview", """
            [ { "ExternalMatchId": 1111, "PublicMatchId": "M100000001" } ]
            """);
        using var http = MaakClient(server);

        var result = await SportlinkClubClient.ResolvePublicMatchIdAsync(
            http, wedstrijdnummer: 3403, datum: new DateOnly(2026, 9, 5), accessToken: "test-token", NullLogger.Instance);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolvePublicMatchIdAsync_ZetDeGevraagdeDatumEnHeadersCorrectDoor()
    {
        using var server = new SportlinkFixtureServer();
        server.RespondWithJson("/competition/match/MatchProgramOverview", "[]");
        using var http = MaakClient(server);

        await SportlinkClubClient.ResolvePublicMatchIdAsync(
            http, wedstrijdnummer: 3403, datum: new DateOnly(2026, 9, 5), accessToken: "test-token", NullLogger.Instance);

        server.Requests.Should().ContainSingle(r =>
            r.StartsWith("/competition/match/MatchProgramOverview") &&
            r.Contains("DateFrom=2026-09-05") && r.Contains("DateTo=2026-09-05"));
    }

    [Fact]
    public async Task GetMatchAsync_ParsedDeBevestigdeVeldenInclusiefPermissievlaggen()
    {
        using var server = new SportlinkFixtureServer();
        server.RespondWithJson("/competition/match/Match", """
            {
              "PublicMatchId": "M392686417",
              "MatchDate": "2026-09-05",
              "MatchStatus": "SCHEDULED",
              "IsCanceledMatch": false,
              "IsConceptMatch": false,
              "IsHomeMatch": true,
              "IsEditFieldAllowed": true,
              "IsAssignDressingRoomsAllowed": true,
              "IsAssignOfficialsAllowed": false,
              "IsAddScoreAllowed": false,
              "TaskStatus": ["MISSING_DRESSINGROOMS"]
            }
            """);
        using var http = MaakClient(server);

        var result = await SportlinkClubClient.GetMatchAsync(http, "M392686417", "test-token", NullLogger.Instance);

        result.Should().NotBeNull();
        result!.PublicMatchId.Should().Be("M392686417");
        result.MatchStatus.Should().Be("SCHEDULED");
        result.IsHomeMatch.Should().BeTrue();
        result.IsAssignDressingRoomsAllowed.Should().BeTrue();
        result.IsAssignOfficialsAllowed.Should().BeFalse();
        result.TaskStatus.Should().ContainSingle("MISSING_DRESSINGROOMS");
    }

    [Fact]
    public async Task GetMatchAsync_ZetDeVerplichteNavajoHeadersDoor()
    {
        using var server = new SportlinkFixtureServer();
        string? ontvangenEntity = null, ontvangenInstance = null, ontvangenLocale = null;
        server.RespondWithJson("/competition/match/Match", req =>
        {
            ontvangenEntity = req.Headers["X-Navajo-Entity"];
            ontvangenInstance = req.Headers["X-Navajo-Instance"];
            ontvangenLocale = req.Headers["X-Navajo-Locale"];
            return """{ "PublicMatchId": "M1" }""";
        });
        using var http = MaakClient(server);

        await SportlinkClubClient.GetMatchAsync(http, "M1", "test-token", NullLogger.Instance);

        ontvangenEntity.Should().Be("competition/match/Match");
        ontvangenInstance.Should().Be("KNVB");
        ontvangenLocale.Should().Be("nl");
    }
}
