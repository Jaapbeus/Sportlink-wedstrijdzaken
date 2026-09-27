using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Autorisatie;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

/// <summary>
/// #1390: de toegangsmatrix accepteert een `RolNaam`/`FeatureKey`-combinatie alleen als beide
/// bekend zijn. Zelfde scenario als
/// <c>FunctionApp.Postgres.Tests/AdminRolFeatureInstellingenValidatieTests.cs</c> op de
/// Postgres-tier.
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
