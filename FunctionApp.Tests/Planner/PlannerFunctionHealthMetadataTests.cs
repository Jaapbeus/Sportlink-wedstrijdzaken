using FluentAssertions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// #863: /api/health's tier-provenance (`tier`/`provider`) komt uit build-time
/// <see cref="System.Reflection.AssemblyMetadataAttribute"/>-waarden
/// (<c>FunctionApp/fa-dev-sportlink-01.csproj</c>), niet uit een runtime-gok — daarom hier
/// rechtstreeks getest zonder database of een nagemaakte HttpRequest/FunctionContext nodig te
/// hebben. "SqlServer" is de canonieke tier-naam uit <c>scripts/ci/database-tiers.json</c> (#816/#865).
/// </summary>
public class PlannerFunctionHealthMetadataTests
{
    [Fact]
    public void GetAssemblyMetadata_DatabaseTier_IsDeCanoniekeSqlServerTierNaam()
    {
        PlannerFunction.GetAssemblyMetadata("DatabaseTier").Should().Be("SqlServer");
    }

    [Fact]
    public void GetAssemblyMetadata_DatabaseProvider_IsDeDaadwerkelijkGebruikteDriver()
    {
        PlannerFunction.GetAssemblyMetadata("DatabaseProvider").Should().Be("Microsoft.Data.SqlClient");
    }

    [Fact]
    public void GetAssemblyMetadata_OnbekendeSleutel_GeeftNullTerug()
    {
        PlannerFunction.GetAssemblyMetadata("NietBestaandeSleutel").Should().BeNull();
    }
}
