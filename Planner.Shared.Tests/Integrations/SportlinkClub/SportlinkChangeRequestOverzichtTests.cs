using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

/// <summary>
/// Unit tests voor <see cref="SportlinkChangeRequestOverzichtItem.Verrijk"/> (#1111) — de pure
/// koppel- en sorteerregel achter <c>GET /api/sportlink/change-requests</c>. Geen database, geen
/// Sportlink.
/// <para>
/// Verhuisd uit <c>FunctionApp.Postgres.Tests</c> bij #1266: sinds het type in Planner.Shared staat
/// is de regel niet meer van één tier. Eén test dekt nu beide tiers.
/// </para>
/// </summary>
public class SportlinkChangeRequestOverzichtTests
{
    private static SportlinkChangeRequest Verzoek(string publicMatchId, string status, string requestId = "R1", bool? inkomend = null) => new()
    {
        PublicMatchId = publicMatchId,
        PublicRequestId = requestId,
        RequestStatus = status,
        Reason = "reden",
        IsIncomingRequest = inkomend,
    };

    private static readonly SportlinkWedstrijdContext Context =
        new(9600001, 3403, "TEST1", "TEST2", "2026-09-05", "14:30", "Sportpark Test");

    [Fact]
    public void Verrijk_BekendPublicMatchId_KoppeltWedstrijdContext()
    {
        var result = SportlinkChangeRequestOverzichtItem.Verrijk(
            new[] { Verzoek("M1", "CONFIRM_HOME") },
            new Dictionary<string, SportlinkWedstrijdContext> { ["M1"] = Context });

        result.Should().ContainSingle();
        result[0].Wedstrijd.Should().Be(Context);
        result[0].PublicMatchId.Should().Be("M1");
        result[0].RequestStatus.Should().Be("CONFIRM_HOME");
        result[0].Reason.Should().Be("reden", "de Sportlink-velden blijven letterlijk staan");
    }

    [Fact]
    public void Verrijk_OnbekendPublicMatchId_LaatVerzoekStaanZonderContext()
    {
        var result = SportlinkChangeRequestOverzichtItem.Verrijk(
            new[] { Verzoek("M-onbekend", "CONFIRM_HOME") },
            new Dictionary<string, SportlinkWedstrijdContext>());

        result.Should().ContainSingle("een verzoek zonder cache-treffer mag nooit uit de lijst verdwijnen");
        result[0].Wedstrijd.Should().BeNull();
    }

    [Fact]
    public void Verrijk_GeeftSportlinkWedstrijdnummerEnTeamsDoor_OokZonderEigenContext()
    {
        var verzoek = Verzoek("M-onbekend", "APPROVED") with
        {
            ExternalMatchId = "84663",
            HomeTeam = new SportlinkChangeRequestTeam { TeamName = "TEST1" },
            AwayTeam = new SportlinkChangeRequestTeam { TeamName = "TEST2" },
        };

        var result = SportlinkChangeRequestOverzichtItem.Verrijk(
            new[] { verzoek }, new Dictionary<string, SportlinkWedstrijdContext>());

        result[0].Wedstrijd.Should().BeNull();
        result[0].ExternalMatchId.Should().Be("84663", "Sportlinks eigen wedstrijdnummer is de terugval (#1464)");
        result[0].Thuisteam.Should().Be("TEST1");
        result[0].Uitteam.Should().Be("TEST2");
    }

    [Fact]
    public void Verrijk_OpenstaandeVerzoekenEerst_VolgordeBinnenGroepBlijftDieVanSportlink()
    {
        var input = new[]
        {
            Verzoek("A", "APPROVED", "R1"),
            Verzoek("B", "CONFIRM_HOME", "R2"),
            Verzoek("C", "DENIED", "R3"),
            Verzoek("D", "confirm_union", "R4"),   // Sportlink-casing niet vertrouwen
            Verzoek("E", "REVOKED", "R5"),
        };

        var result = SportlinkChangeRequestOverzichtItem.Verrijk(input, new Dictionary<string, SportlinkWedstrijdContext>());

        result.Select(r => r.PublicRequestId).Should().Equal("R2", "R4", "R1", "R3", "R5");
    }

    [Theory]
    [InlineData("CONFIRM_AWAY", "OPEN")]
    [InlineData("CONFIRM_HOME", "OPEN")]
    [InlineData("CONFIRM_UNION", "OPEN")]
    [InlineData("APPROVED", "ACCEPTED")]
    [InlineData("MATCH_FINALIZED", "ACCEPTED")]
    [InlineData("DENIED", "DENIED")]
    [InlineData("REVOKED", "REVOKED")]
    [InlineData("revoked", "REVOKED")]
    [InlineData("", "UNKNOWN")]
    [InlineData(null, "UNKNOWN")]
    [InlineData("CONFIRM", "UNKNOWN")]
    [InlineData("XYZ", "UNKNOWN")]
    public void StatusGroep_Bepaal_MapptNaarSportlinkGroepen(string? status, string verwacht)
        => SportlinkChangeRequestStatusGroep.Bepaal(status).Should().Be(verwacht);

    [Fact]
    public void Verrijk_OpenstaandInkomendEerst_DaarnaOpenstaandUitgaand_DanRest()
    {
        var input = new[]
        {
            Verzoek("A", "APPROVED", "R1", true),
            Verzoek("B", "CONFIRM_AWAY", "R2", false),
            Verzoek("C", "CONFIRM_HOME", "R3", true),
            Verzoek("D", "DENIED", "R4", true),
        };

        var result = SportlinkChangeRequestOverzichtItem.Verrijk(input, new Dictionary<string, SportlinkWedstrijdContext>());

        result.Select(r => r.PublicRequestId).Should().Equal("R3", "R2", "R1", "R4");
        result[0].StatusGroep.Should().Be("OPEN");
    }

    [Fact]
    public void Verrijk_GeeftIsIncomingRequestDoor()
    {
        var result = SportlinkChangeRequestOverzichtItem.Verrijk(
            new[] { Verzoek("A", "APPROVED", "R1", false), Verzoek("B", "APPROVED", "R2", null) },
            new Dictionary<string, SportlinkWedstrijdContext>());

        // null telt als inkomend en sorteert dus vóór uitgaand (false).
        result.Select(r => r.IsIncomingRequest).Should().Equal(null, false);
    }

    [Fact]
    public void Verrijk_LegeInvoer_GeeftLegeLijst()
    {
        SportlinkChangeRequestOverzichtItem.Verrijk(
            Array.Empty<SportlinkChangeRequest>(),
            new Dictionary<string, SportlinkWedstrijdContext>()).Should().BeEmpty();
    }
}
