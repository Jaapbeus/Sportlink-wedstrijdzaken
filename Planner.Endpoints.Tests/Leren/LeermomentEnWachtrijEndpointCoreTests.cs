using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Leren;
using Planner.Shared.Leren;
using Xunit;

namespace Planner.Endpoints.Tests.Leren;

/// <summary>Admin-leermoment en wachtrij met onbekende teamteksten (#1568 deel C).</summary>
public class LeermomentEnWachtrijEndpointCoreTests
{
    private const string Club = "TESTCLUB";
    private static readonly LerenAanroeper Wie = new("oid-1", "Testbeheerder");

    [Fact]
    public async Task Leermoment_GeldigeInvoer_WordtGesaneerdOpgeslagenMetAanroeperUitHetPrincipal()
    {
        var store = new FakeLeermomentStore();
        var body = "{\"origineelVerzoekType\":\"BeschikbaarheidCheck\",\"juistVerzoekType\":\"HerplanVerzoek\"," +
                   "\"samenvatting\":\"Wil verzetten, mail jan@voorbeeld.nl\",\"herkomstVerwerkingId\":55,\"aangemaaktDoor\":\"iemand-anders\"}";

        var result = await LeermomentEndpointCore.AanmakenAsync(Club, body, Wie, store.MaakAanAsync);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        var opdracht = store.Opdracht!;
        opdracht.Samenvatting.Should().NotContain("@");
        opdracht.OrigineelType.Should().Be("BeschikbaarheidCheck");
        opdracht.JuistType.Should().Be("HerplanVerzoek");
        opdracht.HerkomstVerwerkingId.Should().Be(55);
        opdracht.Wie.Should().Be(Wie);
        opdracht.ClubCode.Should().Be(Club);
    }

    [Fact]
    public async Task Leermoment_ZonderOrigineelType_KrijgtOnbekend()
    {
        var store = new FakeLeermomentStore();

        await LeermomentEndpointCore.AanmakenAsync(Club, "{\"juistVerzoekType\":\"Bevestiging\",\"samenvatting\":\"akkoord\"}", Wie, store.MaakAanAsync);

        store.Opdracht!.OrigineelType.Should().Be("Onbekend");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("kapot")]
    [InlineData("{\"juistVerzoekType\":\"Onzin\",\"samenvatting\":\"x\"}")]
    [InlineData("{\"juistVerzoekType\":\"Bevestiging\",\"samenvatting\":\"\"}")]
    public async Task Leermoment_OngeldigeInvoer_Geeft400_EnSlaatNietsOp(string? body)
    {
        var store = new FakeLeermomentStore();

        var result = await LeermomentEndpointCore.AanmakenAsync(Club, body, Wie, store.MaakAanAsync);

        result.Should().BeOfType<BadRequestObjectResult>();
        store.Opdracht.Should().BeNull();
    }

    private static IQueryCollection Query(string status, string limit = "") => new QueryCollection(
        new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["status"] = status, ["limit"] = limit });

    [Fact]
    public async Task Wachtrij_Lijst_GeeftItemsEnAantalOpen()
    {
        var wachtrij = new FakeWachtrij();
        wachtrij.Rijen.Add(new OnbekendeTeamTekstRij(1, "j10-04", "J10-04", 3, DateTime.UtcNow, DateTime.UtcNow, 9, "open"));

        var result = await OnbekendeTeamTekstEndpointCore.LijstAsync(Club, Query("open"), wachtrij);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value!.ToString().Should().Contain("open = 1");
        wachtrij.LaatsteLijstStatus.Should().Be("open");
    }

    [Fact]
    public async Task Wachtrij_Lijst_ZonderStatus_ToontAlles_EnOngeldigeStatusGeeft400()
    {
        var wachtrij = new FakeWachtrij();

        await OnbekendeTeamTekstEndpointCore.LijstAsync(Club, Query(""), wachtrij);
        wachtrij.LaatsteLijstStatus.Should().BeNull();

        (await OnbekendeTeamTekstEndpointCore.LijstAsync(Club, Query("onzin"), wachtrij)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("genegeerd")]
    [InlineData("open")]
    [InlineData("afgehandeld")]
    public async Task Wachtrij_ZetStatus_GeldigeStatus_Geeft200(string status)
    {
        var wachtrij = new FakeWachtrij();

        var result = await OnbekendeTeamTekstEndpointCore.ZetStatusAsync(Club, 4, $"{{\"status\":\"{status}\"}}", wachtrij);

        result.Should().BeOfType<OkObjectResult>();
        wachtrij.LaatsteStatus.Should().Be((4, status));
    }

    [Fact]
    public async Task Wachtrij_ZetStatus_OngeldigOfOnbekend_Geeft400Of404()
    {
        var wachtrij = new FakeWachtrij { AantalRijenGeraakt = 0 };

        (await OnbekendeTeamTekstEndpointCore.ZetStatusAsync(Club, 4, "{\"status\":\"vernietigd\"}", wachtrij)).Should().BeOfType<BadRequestObjectResult>();
        (await OnbekendeTeamTekstEndpointCore.ZetStatusAsync(Club, 4, "{\"status\":\"open\"}", wachtrij)).Should().BeOfType<NotFoundObjectResult>();
    }
}
