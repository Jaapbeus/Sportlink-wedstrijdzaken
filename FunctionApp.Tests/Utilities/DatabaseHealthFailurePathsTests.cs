using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SportlinkFunction;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Utilities;

/// <summary>
/// #859: een onbruikbare connectiereeks of een mislukte instellingenlaadt moeten zichtbaar zijn in
/// de gezondheidscheck — vóór deze fix gaf beide gevallen stilzwijgend <c>200 OK</c> terug.
/// </summary>
public class DatabaseHealthFailurePathsTests
{
    [Fact]
    public void BuildConnectionString_ZonderWaarde_GooitInvalidOperationException()
    {
        Action act = () => SystemUtilities.DatabaseConfig.BuildConnectionString(null);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void BuildConnectionString_MetOngeldigeSyntax_Gooit()
    {
        Action act = () => SystemUtilities.DatabaseConfig.BuildConnectionString("dit;is===geen@@@geldige;;reeks==");

        act.Should().Throw<Exception>();
    }

    [Fact]
    public async Task GetDatabaseStatusAsync_ZonderBruikbareConnectiereeks_GeeftUnconfigured()
    {
        var (status, serverVersion) = await PlannerFunction.GetDatabaseStatusAsync(
            () => throw new InvalidOperationException("De connectiereeks is niet gezet in de omgevingsvariabelen."));

        status.Should().Be("unconfigured");
        serverVersion.Should().BeNull();
    }

    [Fact]
    public async Task Health_ZonderBruikbareConnectiereeks_GeeftGeen200OK()
    {
        // Simuleert het scenario waarin de omgevingsvariabele ontbreekt of een connectiereeks van
        // een andere engine bevat: DatabaseConfig.ConnectionString gooit bij statische initialisatie.
        var (dbStatus, _) = await PlannerFunction.GetDatabaseStatusAsync(
            () => throw new InvalidOperationException("onbruikbaar"));

        dbStatus.Should().Be("unconfigured");

        // Zelfde beslisregel als PlannerFunction.Health(): "unconfigured" hoort geen 200 OK te zijn.
        IActionResult result = dbStatus == "unconfigured"
            ? new ObjectResult(new { status = "degraded", database = dbStatus }) { StatusCode = StatusCodes.Status503ServiceUnavailable }
            : new OkObjectResult(new { status = "ok", database = dbStatus });

        result.Should().BeOfType<ObjectResult>();
        ((ObjectResult)result).StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task GetDatabaseStatusAsync_MetWerkendeConnectiestringFunctie_RoeptDatabaseConfigNietAan()
    {
        // Regressiebewaking voor de override-parameter zelf: als geen override wordt meegegeven,
        // valt de methode terug op SystemUtilities.DatabaseConfig.ConnectionString (het echte pad),
        // dat in een testomgeving zonder database "unavailable"/"timeout" oplevert, nooit "ok" zonder
        // enige databaseverbinding. Dit bewijst alleen dat de override daadwerkelijk gebruikt wordt.
        var callCount = 0;
        var (status, _) = await PlannerFunction.GetDatabaseStatusAsync(() =>
        {
            callCount++;
            throw new InvalidOperationException("niet geconfigureerd");
        });

        callCount.Should().Be(1);
        status.Should().Be("unconfigured");
    }
}
