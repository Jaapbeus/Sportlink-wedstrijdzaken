using AwesomeAssertions;
using FunctionApp.Postgres.Sportlink;
using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>De gedeelde endpoint-stappen van #1122 — de statusvertaling die voorheen drie keer
/// gekopieerd stond, en de audit-bepaling die nu op één plek leeft.</summary>
public class SportlinkEndpointSupportTests
{
    [Theory]
    [InlineData(SportlinkClubCallStatus.RolNietGekoppeld, 409)]
    [InlineData(SportlinkClubCallStatus.HerkoppelingVereist, 409)]
    [InlineData(SportlinkClubCallStatus.SportlinkFout, 502)]
    [InlineData(SportlinkClubCallStatus.NetwerkFout, 502)]
    public void VertaalStatusNaarFout_GeeftHttpFoutZonderSportlinkDetails(SportlinkClubCallStatus status, int verwachtHttp)
    {
        var result = SportlinkEndpointSupport.VertaalStatusNaarFout(status) as ObjectResult;

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(verwachtHttp);
        result.Value!.ToString().Should().NotContain("Exception", "nooit interne details richting client");
    }

    [Fact]
    public void VertaalStatusNaarFout_Ok_IsNull()
        => SportlinkEndpointSupport.VertaalStatusNaarFout(SportlinkClubCallStatus.Ok).Should().BeNull();

    [Theory]
    [InlineData(false, true, false, "Success")]
    [InlineData(false, false, false, "Failure")]
    [InlineData(true, true, false, "DryRun")]
    [InlineData(true, true, true, "DryRunLocked")]
    public void BepaalAuditResultaat_CodeLockGaatVoorDryRunVoorSucces(bool dryRun, bool success, bool forced, string verwacht)
        => SportlinkEndpointSupport.BepaalAuditResultaat(new SportlinkMutationResult(success, null, dryRun, forced)).Should().Be(verwacht);
}
