using AwesomeAssertions;
using FunctionApp.Postgres.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Unit tests voor <see cref="SportlinkChangeRequestOverzichtItem.Verrijk"/> (#1111) — de pure
/// koppel- en sorteerregel achter <c>GET /api/sportlink/change-requests</c>. Geen database, geen
/// Sportlink.
/// </summary>
public class SportlinkChangeRequestOverzichtTests
{
    private static SportlinkChangeRequest Verzoek(string publicMatchId, string status, string requestId = "R1") => new()
    {
        PublicMatchId = publicMatchId,
        PublicRequestId = requestId,
        RequestStatus = status,
        Reason = "reden",
    };

    private static readonly SportlinkWedstrijdContext Context =
        new(9600001, 3403, "TEST1", "TEST2", "2026-09-05", "14:30", "Sportpark Test");

    [Fact]
    public void Verrijk_BekendPublicMatchId_KoppeltWedstrijdContext()
    {
        var result = SportlinkChangeRequestOverzichtItem.Verrijk(
            new[] { Verzoek("M1", "CONFIRM") },
            new Dictionary<string, SportlinkWedstrijdContext> { ["M1"] = Context });

        result.Should().ContainSingle();
        result[0].Wedstrijd.Should().Be(Context);
        result[0].PublicMatchId.Should().Be("M1");
        result[0].RequestStatus.Should().Be("CONFIRM");
        result[0].Reason.Should().Be("reden", "de Sportlink-velden blijven letterlijk staan");
    }

    [Fact]
    public void Verrijk_OnbekendPublicMatchId_LaatVerzoekStaanZonderContext()
    {
        var result = SportlinkChangeRequestOverzichtItem.Verrijk(
            new[] { Verzoek("M-onbekend", "CONFIRM") },
            new Dictionary<string, SportlinkWedstrijdContext>());

        result.Should().ContainSingle("een verzoek zonder cache-treffer mag nooit uit de lijst verdwijnen");
        result[0].Wedstrijd.Should().BeNull();
    }

    [Fact]
    public void Verrijk_OpenstaandeVerzoekenEerst_VolgordeBinnenGroepBlijftDieVanSportlink()
    {
        var input = new[]
        {
            Verzoek("A", "APPROVED", "R1"),
            Verzoek("B", "CONFIRM", "R2"),
            Verzoek("C", "DENIED", "R3"),
            Verzoek("D", "confirm", "R4"),   // Sportlink-casing niet vertrouwen
            Verzoek("E", "REVOKED", "R5"),
        };

        var result = SportlinkChangeRequestOverzichtItem.Verrijk(input, new Dictionary<string, SportlinkWedstrijdContext>());

        result.Select(r => r.PublicRequestId).Should().Equal("R2", "R4", "R1", "R3", "R5");
    }

    [Fact]
    public void Verrijk_LegeInvoer_GeeftLegeLijst()
    {
        SportlinkChangeRequestOverzichtItem.Verrijk(
            Array.Empty<SportlinkChangeRequest>(),
            new Dictionary<string, SportlinkWedstrijdContext>()).Should().BeEmpty();
    }
}
