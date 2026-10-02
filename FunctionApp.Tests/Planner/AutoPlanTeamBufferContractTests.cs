using System.Text.Json;
using AwesomeAssertions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// #1430: de handmatige conflictcontrole in de Admin GUI leest per wedstrijd de ruwe teamregels
/// <c>teamBufferVoor</c>/<c>teamBufferNa</c> uit het auto-plancontract. Deze test legt de draadvorm
/// van de SQL Server-tier vast; <c>FunctionApp.Postgres.Tests</c> doet hetzelfde voor de Postgres-tier,
/// zodat beide tiers contractueel dezelfde velden leveren.
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
