using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Planner.Endpoints.Admin;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

/// <summary>#1779: gedeelde 200/207-keuze van /sync-matches en /postgres/sync-matches (pariteit, #1081).</summary>
public class SyncMatchesAntwoordCoreTests
{
    [Fact]
    public void AlleFasesGeslaagd_Geeft200MetDeTekstVanDeTier()
    {
        var result = SyncMatchesAntwoordCore.Bepaal(new SyncUitkomst(), -1, 42, "Sync voltooid.");

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be("Sync voltooid.");
    }

    [Fact]
    public void MislukteFase_Geeft207MetFasesEnVenster()
    {
        var uitkomst = new SyncUitkomst();
        uitkomst.Registreer(SyncUitkomst.FaseMatchdetails, true);

        var result = SyncMatchesAntwoordCore.Bepaal(uitkomst, -1, 42, "Sync voltooid.");

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status207MultiStatus);
        var body = JObject.FromObject(obj.Value!);
        body["status"]!.Value<string>().Should().Be("gedeeltelijk mislukt");
        body["weekOffsetFrom"]!.Value<int>().Should().Be(-1);
        body["weekOffsetTo"]!.Value<int>().Should().Be(42);
        body["mislukteFases"]!.ToObject<string[]>().Should().Equal("matchdetails");
        body["melding"]!.Value<string>().Should().Be(SyncMatchesAntwoordCore.GedeeltelijkMisluktMelding);
    }

    [Fact]
    public void OntbrekendeConfiguratie_Geeft207MetFaseConfiguratie()
    {
        var result = SyncMatchesAntwoordCore.Bepaal(SyncUitkomst.Configuratie(), -1, 42, "Sync voltooid.");

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status207MultiStatus);
        JObject.FromObject(obj.Value!)["mislukteFases"]!.ToObject<string[]>().Should().Equal("configuratie");
    }
}
