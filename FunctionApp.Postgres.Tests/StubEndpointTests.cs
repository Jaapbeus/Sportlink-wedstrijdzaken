using FluentAssertions;
using FunctionApp.Postgres.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Bewaakt dat een nog-niet-vertaald endpoint een <b>expliciete 501</b> geeft — geen stille 404 en
/// geen onjuiste 200.
///
/// <para>
/// <b>Deze klasse verving <c>PlannerFunctionStubTests</c> bij issue 888 vervolg (§42).</b> Die
/// bewaakte destijds negen planner-stubs; die zijn inmiddels allemaal echte implementaties (zie
/// <c>AutoPlanServiceIntegrationTests</c> en <c>AvailabilityServiceIntegrationTests</c> voor hun
/// dekking tegen een echte Postgres-container). Er is nog precies één stub over op deze tier —
/// <c>AdminTeambegeleidingDoorsturen</c> — en die blijft hier bewaakt tot ook hij vertaald is.
/// </para>
///
/// <para>
/// <b>Waarom geen functiehost of database nodig is.</b> De stub roept alleen
/// <c>EasyAuthHelper.RequireAdmin</c> aan (pure headerinspectie) en retourneert daarna
/// onvoorwaardelijk 501 — <c>FunctionContext</c> wordt nergens gebruikt.
/// </para>
/// </summary>
public class StubEndpointTests
{
    private static HttpRequest MaakRequest() => new DefaultHttpContext().Request;

    [Fact]
    public async Task Doorsturen_GeeftExplicieteVijfhonderdEen()
    {
        var resultaat = await AdminTeambegeleidingFunction.Doorsturen(MaakRequest(), null!);

        var obj = resultaat.Should().BeOfType<ObjectResult>(
            "Doorsturen moet een ObjectResult teruggeven, geen 404/200").Subject;
        obj.StatusCode.Should().Be(501,
            "Doorsturen is niet vertaald en mag geen andere statuscode voorwenden");
    }

    [Fact]
    public async Task Doorsturen_NoemtDeDaadwerkelijkeOntbrekendeAfhankelijkheid()
    {
        // De inhoud van de melding is net zo belangrijk als de statuscode — een generieke reden
        // zou een toekomstige lezer misleiden over wat er precies ontbreekt.
        var resultaat = (ObjectResult)await AdminTeambegeleidingFunction.Doorsturen(MaakRequest(), null!);
        var tekst = resultaat.Value!.ToString();

        tekst.Should().Contain("GraphServiceClient",
            "de melding moet de daadwerkelijke ontbrekende afhankelijkheid noemen, niet 'nog niet gedaan'");
    }
}
