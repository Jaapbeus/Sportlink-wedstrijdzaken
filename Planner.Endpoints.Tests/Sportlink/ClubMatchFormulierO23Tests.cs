using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Planner.Endpoints.Sportlink.ClubMatchEndpointCore;

namespace Planner.Endpoints.Tests.Sportlink;

/// <summary>
/// #1863: het aanmaakformulier vult voor een O23-team de JO23-speeltijd voor, ook als Sportlink het team als
/// "Senioren" (1-99) levert; zonder bruikbare JO23-rij (ontbrekend of duur 0) geldt de categorie, nooit duur 0.
/// </summary>
public class ClubMatchFormulierO23Tests
{
    private static async Task<int?> DuurAsync(string team, string? categorie, params ClubMatchSpeeltijdInvoer[] speeltijden)
    {
        var formulier = await BouwFormulierAsync(null, "rol", new[] { new ClubMatchFormulierTeamInvoer(team, categorie) },
            speeltijden, NullLogger.Instance);
        return formulier.Teams.Single().Duur;
    }

    private static readonly ClubMatchSpeeltijdInvoer Senioren = new("1-99", 1.0m, 90);

    [Fact]
    public async Task O23MetSeniorencategorie_KrijgtJo23Duur()
        => (await DuurAsync("TESTCLUB O23-1", "1-99", Senioren, new("JO23", 1.0m, 105))).Should().Be(105);

    [Fact]
    public async Task O23MetJo23DuurNul_ValtTerugOpCategorie()
        => (await DuurAsync("TESTCLUB O23-1", "1-99", Senioren, new("JO23", 1.0m, 0))).Should().Be(90);

    [Fact]
    public async Task O23ZonderJo23Rij_ValtTerugOpCategorie()
        => (await DuurAsync("TESTCLUB O23-1", "1-99", Senioren)).Should().Be(90);

    [Fact]
    public async Task Mo23Team_KrijgtGeenJo23Duur()
        => (await DuurAsync("TESTCLUB MO23-1", "1-99", Senioren, new("JO23", 1.0m, 105))).Should().Be(90);

    [Fact]
    public async Task EersteElftal_BlijftSeniorenDuur()
        => (await DuurAsync("TESTCLUB 1", "1-99", Senioren, new("JO23", 1.0m, 105))).Should().Be(90);
}
