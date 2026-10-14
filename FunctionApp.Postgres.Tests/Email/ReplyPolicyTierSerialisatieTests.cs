using AwesomeAssertions;
using Newtonsoft.Json;
using FunctionApp.Postgres.Planner;
using Planner.Shared.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Koppeling van de gedeelde <see cref="ReplyPolicy"/> (#1760) met de plannerrespons van deze tier: de
/// echte <c>CheckAvailabilityResponse</c> wordt geserialiseerd zoals in <c>BerichtPipeline</c>, zodat een
/// wijziging in veldnamen of serialisatie hier opvalt in plaats van stil de policy te breken. De gevallen
/// van de policy zelf staan in <c>Planner.Shared.Tests</c> (<c>ReplyPolicyTests</c>).
/// </summary>
public class ReplyPolicyTierSerialisatieTests
{
    private static BerichtClassificatie Beschikbaarheid() =>
        new() { Type = VerzoekType.BeschikbaarheidCheck, Datum = "2026-09-12", TeamNaam = "TEST JO14-1" };

    [Fact]
    public void EnkeleDatum_PlanningMogelijk_GeenAntwoord()
    {
        var json = JsonConvert.SerializeObject(new CheckAvailabilityResponse { Beschikbaar = true });

        ReplyPolicy.Bepaal(Beschikbaarheid(), json).Actie.Should().Be(ReplyActie.Onderdrukken);
    }

    [Fact]
    public void EnkeleDatum_PlanningNietMogelijk_WelAntwoord()
    {
        var json = JsonConvert.SerializeObject(new CheckAvailabilityResponse { Beschikbaar = false, Reden = "Geen beschikbaar veld gevonden." });

        ReplyPolicy.Bepaal(Beschikbaarheid(), json).Actie.Should().Be(ReplyActie.Versturen);
    }

    [Fact]
    public void MultiDatum_GemengdeUitkomst_WelAntwoord()
    {
        var resultaten = new[] { true, false }.Select((b, i) => (object)new
        {
            datum = new DateOnly(2026, 9, 7).AddDays(i).ToString("yyyy-MM-dd"),
            response = new CheckAvailabilityResponse { Beschikbaar = b }
        }).ToList();
        var json = JsonConvert.SerializeObject(new { multiDatum = true, resultaten });

        var besluit = ReplyPolicy.Bepaal(Beschikbaarheid(), json);

        besluit.Actie.Should().Be(ReplyActie.Versturen);
        besluit.Reden.Should().Contain("Gemengde uitkomst");
    }
}
