using AwesomeAssertions;
using FunctionApp.Postgres.Admin;
using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Autorisatie;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #1390: de toegangsmatrix accepteert een `RolNaam`/`FeatureKey`-combinatie alleen als beide
/// bekend zijn — anders kan een tikfout in de client stilzwijgend een dode rij aanmaken die nooit
/// door <c>GetMatrixAsync</c> wordt teruggegeven (fail-closed, dus onopgemerkt). Zelfde scenario
/// als <c>FunctionApp.Tests/Admin/AdminRolFeatureInstellingenValidatieTests.cs</c> op de SQL
/// Server-tier.
/// </summary>
public class AdminRolFeatureInstellingenValidatieTests
{
    [Fact]
    public void ValideerRolEnFeatureKey_OnbekendeRol_WordtAfgewezen()
    {
        var result = AdminRolFeatureInstellingenFunction.ValideerRolEnFeatureKey("Penningmeester", MenuFeatureKeys.Dashboard);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void ValideerRolEnFeatureKey_AdminAlsRol_WordtAfgewezen()
    {
        // 'admin' is bewust geen instelbare rij — anders zou een club 'admin' per ongeluk kunnen
        // beperken, wat #1341/#1390 expliciet uitsluiten (admin heeft altijd alles aan).
        var result = AdminRolFeatureInstellingenFunction.ValideerRolEnFeatureKey("admin", MenuFeatureKeys.Dashboard);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void ValideerRolEnFeatureKey_OnbekendeFeatureKey_WordtAfgewezen()
    {
        var result = AdminRolFeatureInstellingenFunction.ValideerRolEnFeatureKey(RolNamen.Sectiehoofd, "menu.onbestaand");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void ValideerRolEnFeatureKey_OntbrekendeWaarden_WordtAfgewezen()
    {
        var result = AdminRolFeatureInstellingenFunction.ValideerRolEnFeatureKey(null, null);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(RolNamen.User)]
    [InlineData(RolNamen.Wedstrijdzaken)]
    [InlineData(RolNamen.Sectiehoofd)]
    [InlineData(RolNamen.Ledenadministratie)]
    public void ValideerRolEnFeatureKey_GeldigeRolMetMenuFeatureKey_WordtGeaccepteerd(string rolNaam)
    {
        var result = AdminRolFeatureInstellingenFunction.ValideerRolEnFeatureKey(rolNaam, MenuFeatureKeys.Teambegeleiding);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(SportlinkRolFeature.Kleedkamers)]
    [InlineData(SportlinkRolFeature.Scheidsrechter)]
    [InlineData(SportlinkRolFeature.Veld)]
    public void ValideerRolEnFeatureKey_GeldigeSportlinkFeatureKey_WordtGeaccepteerd(string featureKey)
    {
        var result = AdminRolFeatureInstellingenFunction.ValideerRolEnFeatureKey(RolNamen.Wedstrijdzaken, featureKey);

        result.Should().BeNull();
    }
}
