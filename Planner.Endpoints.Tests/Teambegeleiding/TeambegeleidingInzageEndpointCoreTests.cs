using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Teambegeleiding;
using Planner.Shared.Teambegeleiding;
using Xunit;

namespace Planner.Endpoints.Tests.Teambegeleiding;

/// <summary>
/// #1701: het ophalen van contactgegevens van teambegeleiding legt een inzageregel vast, zonder
/// gegevens van de ingeziene begeleiders, en geeft geen gegevens terug als dat vastleggen mislukt.
/// Testdata uitsluitend uit ARCHITECTUUR.md §8.1.5.
/// </summary>
public class TeambegeleidingInzageEndpointCoreTests
{
    private static readonly TeambegeleidingInzager Wie = new("00000000-0000-0000-0000-00000000000a", "Beheerder Test");

    private static readonly TeambegeleiderContact[] Twee =
    [
        new("Jan de Vries", "Trainer", "trainer@voorbeeld.nl", "0000-000001", "Technische staf - Trainer/coach"),
        new("Jan de Vries", "Teamleider", "teamleider@allstars-fc.test", "0000-000002", null),
    ];

    private sealed class NepOpslag(IReadOnlyList<TeambegeleiderContact> contacten, bool logFaalt = false) : ITeambegeleidingInzageStore
    {
        public List<TeambegeleidingInzageNieuw> Gelogd { get; } = [];
        public List<string> Volgorde { get; } = [];

        public Task<IReadOnlyList<TeambegeleiderContact>> HaalContactgegevensAsync(string clubCode, string team)
        {
            Volgorde.Add("haal");
            return Task.FromResult(contacten);
        }

        public Task LogInzageAsync(TeambegeleidingInzageNieuw inzage)
        {
            Volgorde.Add("log");
            if (logFaalt) throw new InvalidOperationException("database niet bereikbaar");
            Gelogd.Add(inzage);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Opvragen_LegtPreciesEenInzageregelVast_MetClubTeamInzienerEnAantal()
    {
        var opslag = new NepOpslag(Twee);

        await TeambegeleidingInzageEndpointCore.ContactgegevensAsync("ALLSTARS", "JO11-1", Wie, opslag);

        var regel = opslag.Gelogd.Should().ContainSingle().Subject;
        regel.ClubCode.Should().Be("ALLSTARS");
        regel.Team.Should().Be("JO11-1");
        regel.AantalRijen.Should().Be(2);
        regel.InzienDoorObjectId.Should().Be(Wie.ObjectId);
        regel.InzienDoorNaam.Should().Be(Wie.Naam);
    }

    [Fact]
    public async Task Opvragen_GeeftDeContactgegevensOngewijzigdTerug()
    {
        var resultaat = await TeambegeleidingInzageEndpointCore.ContactgegevensAsync("ALLSTARS", "JO11-1", Wie, new NepOpslag(Twee));

        var ok = resultaat.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeEquivalentTo(Twee);

        // Het API-contract (camelCase) is hetzelfde als het anonieme object van vóór #1701.
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement[0].EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("naam", "teamrol", "emailadres", "telefoonnummer", "functie");
    }

    [Fact]
    public async Task Inzageregel_BevatGeenEnkelGegevenVanDeIngezienBegeleiders()
    {
        var opslag = new NepOpslag(Twee);

        await TeambegeleidingInzageEndpointCore.ContactgegevensAsync("ALLSTARS", "JO11-1", Wie, opslag);

        var regelAlsTekst = JsonSerializer.Serialize(opslag.Gelogd);
        foreach (var c in Twee)
        {
            foreach (var waarde in new[] { c.Naam, c.Emailadres, c.Telefoonnummer, c.Functie }.OfType<string>())
                regelAlsTekst.Should().NotContain(waarde, "een inzageregel bevat nooit persoonsgegevens van de ingeziene begeleider");
        }
    }

    [Fact]
    public async Task OokEenTeamZonderBegeleidersWordtGelogd_MetNulRijen()
    {
        var opslag = new NepOpslag([]);

        await TeambegeleidingInzageEndpointCore.ContactgegevensAsync("ALLSTARS", "JO7-3", Wie, opslag);

        opslag.Gelogd.Should().ContainSingle().Which.AantalRijen.Should().Be(0);
    }

    [Fact]
    public async Task MisluktHetVastleggen_DanGaanErGeenGegevensTerug()
    {
        var opslag = new NepOpslag(Twee, logFaalt: true);

        var actie = () => TeambegeleidingInzageEndpointCore.ContactgegevensAsync("ALLSTARS", "JO11-1", Wie, opslag);

        await actie.Should().ThrowAsync<InvalidOperationException>("fail-closed: geen contactgegevens zonder inzageregel");
        opslag.Volgorde.Should().Equal("haal", "log");
    }

    [Fact]
    public async Task EenTeLangeTeamnaamUitDeRoute_WordtIngekort_EnLaatDeOpvragingNietMislukken()
    {
        var opslag = new NepOpslag([]);

        await TeambegeleidingInzageEndpointCore.ContactgegevensAsync("ALLSTARS", new string('T', 500), Wie, opslag);

        opslag.Gelogd.Single().Team.Should().HaveLength(TeambegeleidingInzageNieuw.MaxTeam);
    }
}
