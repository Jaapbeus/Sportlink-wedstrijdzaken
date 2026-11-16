using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Admin;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

public sealed class AdminSettingsResponseCoreTests
{
    [Fact]
    public void PutResponse_BewaartVeldnamenNullsEnHerstartBetekenis()
    {
        var response = AdminSettingsResponseCore.BouwPutResponse(
            ["EmailReviewModusActief"], false, false, null, null)
            .Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value)).RootElement;

        json.GetProperty("gewijzigdeVelden")[0].GetString().Should().Be("EmailReviewModusActief");
        json.GetProperty("herstartVereist").GetBoolean().Should().BeFalse();
        json.GetProperty("herstartAutomatisch").GetBoolean().Should().BeFalse();
        json.GetProperty("opmerking").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("fetchScheduleLeesbaar").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("volgendeMomenten").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void CronPreview_VultAlleenBijIngesteldeSchedule()
    {
        var ontbreekt = new Dictionary<string, object?>();
        AdminSettingsResponseCore.VulCronPreview(ontbreekt);
        ontbreekt.Should().BeEmpty();

        var aanwezig = new Dictionary<string, object?> { ["FetchSchedule"] = "0 0 4 * * *" };
        AdminSettingsResponseCore.VulCronPreview(aanwezig);
        aanwezig.Should().ContainKeys("fetchScheduleLeesbaar", "volgendeMomenten");
        aanwezig["volgendeMomenten"].Should().BeOfType<List<string>>()
            .Subject.Should().HaveCount(3);
    }
}
