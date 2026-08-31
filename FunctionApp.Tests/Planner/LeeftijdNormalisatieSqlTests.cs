using FluentAssertions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// Alleen de SQL Server-SQL-generatie wordt hier getoetst. De pure C#-normalisatie is naar
/// <c>Planner.Shared.LeeftijdNormalisatie</c> verhuisd (#889) en wordt daar getest, samen met de
/// invariant dat beide SQL-varianten hetzelfde resultaat horen te geven.
/// </summary>
public class LeeftijdNormalisatieSqlTests
{
    [Fact]
    public void SqlExpr_BevatExplicieteSeniorenCases()
    {
        var expr = LeeftijdNormalisatieSql.SqlExpr("t.[leeftijdscategorie]");

        expr.Should().Contain("SENIOREN");
        expr.Should().Contain("'1-99'");
        expr.Should().Contain("'VR'");
    }
}
