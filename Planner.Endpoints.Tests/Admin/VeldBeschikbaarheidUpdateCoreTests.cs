using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Planner.Endpoints.Admin;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

public sealed class VeldBeschikbaarheidUpdateCoreTests
{
    [Theory]
    [InlineData(null, null, "VeldNaam verplicht")]
    [InlineData(" ", "kunstgras", "VeldNaam verplicht")]
    [InlineData("Veld 1", null, "VeldType verplicht (vrije tekst, bijv. kunstgras of natuurgras)")]
    [InlineData("Veld 1", "  ", "VeldType verplicht (vrije tekst, bijv. kunstgras of natuurgras)")]
    [InlineData("Veld 1", "vrije invoer", null)]
    public void ValideerVeldOmschrijvingen_BehoudtVolgordeEnVrijeTekst(string? naam, string? type, string? fout)
    {
        var result = VeldBeschikbaarheidUpdateCore.ValideerVeldOmschrijvingen(naam, type);
        if (fout is null) Assert.Null(result);
        else Assert.Equal(fout, JObject.FromObject(Assert.IsType<BadRequestObjectResult>(result).Value!)["error"]?.Value<string>());
    }

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
