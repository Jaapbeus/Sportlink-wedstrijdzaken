using FluentAssertions;
using FunctionApp.Postgres.Planner;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt vast dat de negen niet-vertaalde planner-endpoints een expliciete 501 geven — geen stille
/// 404, en geen onjuiste 200 (#888).
///
/// <para>
/// <b>Waarom geen echte functiehost of database nodig is.</b> Elke stub roept alleen
/// <c>EasyAuthHelper.RequireAdmin</c> aan (pure headerinspectie) en retourneert daarna
/// onvoorwaardelijk 501 — <c>FunctionContext</c> wordt nergens gebruikt. Deze test roept de
/// statische methoden dus rechtstreeks aan met een <see cref="DefaultHttpContext"/>, precies
/// zoals de methode zelf hem behandelt. Dat maakt deze dekking permanent en snel (geen Azurite,
/// geen Postgres-container) in plaats van een eenmalige handmatige controle.
/// </para>
///
/// <para>
/// <b>De lokale bypass in <c>EasyAuthHelper.RequireRole</c></b> (afwezigheid van
/// <c>WEBSITE_SITE_NAME</c> → altijd toestaan) is al bewezen in de zelftest-G5/G6-run van #932;
/// deze test leunt bewust op datzelfde, al geverifieerde gedrag in plaats van het opnieuw te
/// bewijzen.
/// </para>
/// </summary>
public class PlannerFunctionStubTests
{
    private static HttpRequest MaakRequest() => new DefaultHttpContext().Request;

    public static IEnumerable<object[]> Stubs()
    {
        yield return new object[] { "CheckAvailability", (Func<HttpRequest, Task<IActionResult>>)(r => PlannerFunction.CheckAvailability(r, null!)) };
        yield return new object[] { "DoordeweeksBeschikbaar", (Func<HttpRequest, Task<IActionResult>>)(r => PlannerFunction.DoordeweeksBeschikbaar(r, null!)) };
        yield return new object[] { "HerplanCheck", (Func<HttpRequest, Task<IActionResult>>)(r => PlannerFunction.HerplanCheck(r, null!)) };
        yield return new object[] { "BevestigWedstrijd", (Func<HttpRequest, Task<IActionResult>>)(r => PlannerFunction.BevestigWedstrijd(r, null!)) };
        yield return new object[] { "ZoekWedstrijd", (Func<HttpRequest, Task<IActionResult>>)(r => PlannerFunction.ZoekWedstrijd(r, null!)) };
        yield return new object[] { "HerplanBevestig", (Func<HttpRequest, Task<IActionResult>>)(r => PlannerFunction.HerplanBevestig(r, null!)) };
        yield return new object[] { "PopulateSunset", (Func<HttpRequest, Task<IActionResult>>)(r => PlannerFunction.PopulateSunset(r, null!)) };
        yield return new object[] { "AutoPlan", (Func<HttpRequest, Task<IActionResult>>)(r => PlannerFunction.AutoPlan(r, null!)) };
        yield return new object[] { "AutoPlanToepassen", (Func<HttpRequest, Task<IActionResult>>)(r => PlannerFunction.AutoPlanToepassen(r, null!)) };
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task GeeftExplicieteVijfhonderdEen(string naam, Func<HttpRequest, Task<IActionResult>> aanroep)
    {
        var resultaat = await aanroep(MaakRequest());

        var obj = resultaat.Should().BeOfType<ObjectResult>($"{naam} moet een ObjectResult teruggeven, geen 404/200").Subject;
        obj.StatusCode.Should().Be(501, $"{naam} is niet vertaald en mag geen andere statuscode voorwenden");
    }

    [Fact]
    public async Task DeDrieAvailabilityAfhankelijkeEndpoints_NoemenBeidePlannerAvailabilityRepositoryEnTeamRulesRepository()
    {
        // De inhoud van de 501-melding is net zo belangrijk als de statuscode — een lege of
        // generieke reden zou een toekomstige lezer misleiden over wat er precies ontbreekt.
        foreach (var naam in new[] { "CheckAvailability", "DoordeweeksBeschikbaar", "HerplanCheck" })
        {
            var aanroep = Stubs().First(s => (string)s[0] == naam)[1];
            var resultaat = await ((Func<HttpRequest, Task<IActionResult>>)aanroep)(MaakRequest());
            var tekst = ((ObjectResult)resultaat).Value!.ToString();

            tekst.Should().Contain("PlannerAvailabilityRepository", naam);
            tekst.Should().Contain("TeamRulesRepository", naam);
        }
    }

    [Fact]
    public async Task DeTweeOntbrekendeTabellen_WordenBijNaamGenoemd()
    {
        var sunset = (ObjectResult)await PlannerFunction.PopulateSunset(MaakRequest(), null!);
        sunset.Value!.ToString().Should().Contain("Zonsondergang");

        var herplan = (ObjectResult)await PlannerFunction.HerplanBevestig(MaakRequest(), null!);
        herplan.Value!.ToString().Should().Contain("HerplanVerzoeken");
    }

    [Fact]
    public async Task DeAutoPlanEndpoints_NoemenDeFieldSchedulerBeslissing()
    {
        var autoPlan = (ObjectResult)await PlannerFunction.AutoPlan(MaakRequest(), null!);
        autoPlan.Value!.ToString().Should().Contain("FieldScheduler");

        var toepassen = (ObjectResult)await PlannerFunction.AutoPlanToepassen(MaakRequest(), null!);
        toepassen.Value!.ToString().Should().Contain("FieldScheduler");
    }
}
