using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Admin;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

/// <summary>#1492: de gedeelde orkestratie van de sync-trigger voor beide tiers.</summary>
public class SyncTriggerEndpointCoreTests
{
    [Fact]
    public async Task ZonderBody_GeeftStandaardVenster_ZonderStartvraag()
    {
        var v = await SyncTriggerEndpointCore.BepaalVensterAsync(null, 2026, () => Task.FromResult(30),
            _ => throw new InvalidOperationException("niet verwacht"));
        v.Fout.Should().BeNull();
        (v.Van, v.Tot, v.IsReset).Should().Be((-1, 30, false));
    }

    [Fact]
    public async Task OngeldigeBody_Geeft400_ZonderDatabasevraag()
    {
        var geraakt = false;
        var v = await SyncTriggerEndpointCore.BepaalVensterAsync("{kapot", 2026,
            () => { geraakt = true; return Task.FromResult(0); }, _ => Task.FromResult<int?>(1));
        v.Fout.Should().BeOfType<BadRequestObjectResult>();
        geraakt.Should().BeFalse();
    }

    [Fact]
    public async Task Reset_GebruiktSeizoensStart()
    {
        var v = await SyncTriggerEndpointCore.BepaalVensterAsync("{\"reset\":true,\"season\":2025}", 2026,
            () => Task.FromResult(40), jaar => Task.FromResult<int?>(jaar == 2025 ? -60 : null));
        v.Fout.Should().BeNull();
        (v.Van, v.Tot, v.IsReset).Should().Be((-60, 40, true));
    }

    [Fact]
    public async Task OnbekendSeizoen_Geeft400()
    {
        var v = await SyncTriggerEndpointCore.BepaalVensterAsync("{\"reset\":true,\"season\":2025}", 2026,
            () => Task.FromResult(40), _ => Task.FromResult<int?>(null));
        v.Fout.Should().BeOfType<BadRequestObjectResult>();
    }
}
