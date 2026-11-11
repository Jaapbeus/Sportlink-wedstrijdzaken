using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Planner.Endpoints.Admin;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

public sealed class VeldBeschikbaarheidUpdateCoreTests
{
    [Fact]
    public async Task ConflicterendeCombinatieAsync_BestaandeAndereRij_Geeft409MetMelding()
    {
        var result = await VeldBeschikbaarheidUpdateCore.ConflicterendeCombinatieAsync(() => Task.FromResult(true));

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal("Combinatie veld + dag + periode bestaat al", JObject.FromObject(conflict.Value!)["error"]?.Value<string>());
    }

    [Fact]
    public async Task ConflicterendeCombinatieAsync_GeenAndereRij_LaatUpdateDoor()
        => Assert.Null(await VeldBeschikbaarheidUpdateCore.ConflicterendeCombinatieAsync(() => Task.FromResult(false)));
}
