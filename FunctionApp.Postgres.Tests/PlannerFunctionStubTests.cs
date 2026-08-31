using FluentAssertions;
using FunctionApp.Postgres.Planner;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt vast dat de twee resterende niet-vertaalde planner-endpoints (<c>AutoPlan</c>,
/// <c>AutoPlanToepassen</c>) een expliciete 501 geven — geen stille 404, en geen onjuiste 200
/// (#888). <c>BevestigWedstrijd</c>, <c>ZoekWedstrijd</c>, <c>HerplanBevestig</c> (#888 vervolg) en
/// sinds §41 ook <c>CheckAvailability</c>, <c>DoordeweeksBeschikbaar</c>, <c>HerplanCheck</c> en
/// <c>PopulateSunset</c> stonden hier eerder ook in; die zijn nu echte implementaties (zie
/// <c>PlannerMatchRepositoryIntegrationTests</c>/<c>AvailabilityServiceIntegrationTests</c> voor hun
/// dekking tegen een echte Postgres-container — een <c>FunctionContext</c>-loze aanroep zoals hier
/// zou daar meteen op een <c>NullReferenceException</c> uit <c>context.GetLogger(...)</c> stuklopen).
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
    public async Task DeAutoPlanEndpoints_NoemenDeFieldSchedulerBeslissing()
    {
        var autoPlan = (ObjectResult)await PlannerFunction.AutoPlan(MaakRequest(), null!);
        autoPlan.Value!.ToString().Should().Contain("FieldScheduler");

        var toepassen = (ObjectResult)await PlannerFunction.AutoPlanToepassen(MaakRequest(), null!);
        toepassen.Value!.ToString().Should().Contain("FieldScheduler");
    }
}
