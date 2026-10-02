using System.Text.Json;
using AwesomeAssertions;
using FunctionApp.Postgres.Planner;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #1430: tegenhanger van <c>FunctionApp.Tests/Planner/AutoPlanTeamBufferContractTests</c> voor de
/// Postgres-tier — beide tiers moeten dezelfde draadvorm leveren. Dat de waarden ook uit
/// <c>public.teamregels</c> komen, bewijst
/// <see cref="AutoPlanServiceIntegrationTests.AutoPlanAsync_LevertTeamspecifiekeBuffersInHetContract"/>.
/// </summary>
public class AutoPlanTeamBufferContractTests
{
    [Fact]
    public void AutoPlanWedstrijdItem_SerialiseertTeamBuffersMetDeNamenDieDeClientVerwacht()
    {
        var json = JsonSerializer.Serialize(
            new AutoPlanWedstrijdItem { TeamBufferVoor = 45, TeamBufferNa = null },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"teamBufferVoor\":45").And.Contain("\"teamBufferNa\":null");
    }
}
