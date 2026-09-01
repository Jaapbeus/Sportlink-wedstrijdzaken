using System;
using FluentAssertions;
using FunctionApp.Postgres;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #859: een onbruikbare connectiereeks of een mislukte instellingenlaadt moeten zichtbaar zijn in
/// de gezondheidscheck — vóór deze fix gaf beide gevallen stilzwijgend <c>200 OK</c> terug. Zelfde
/// scenario als <c>FunctionApp.Tests/Utilities/DatabaseHealthFailurePathsTests.cs</c> op de SQL
/// Server-tier, apart getest omdat het twee volledig losse implementatiebomen zijn (#815 §2).
/// </summary>
public class DatabaseHealthFailurePathsTests
{
    [Fact]
    public void BuildConnectionString_ZonderWaarde_GooitInvalidOperationException()
    {
        Action act = () => PostgresDatabaseConfig.BuildConnectionString(null);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void BuildConnectionString_MetOngeldigeSyntax_Gooit()
    {
        Action act = () => PostgresDatabaseConfig.BuildConnectionString("dit;is===geen@@@geldige;;reeks==");

        act.Should().Throw<Exception>();
    }

    [Fact]
    public async System.Threading.Tasks.Task GetDatabaseStatusAsync_ZonderBruikbareConnectiereeks_GeeftUnconfigured()
    {
        var (status, serverVersion) = await HealthFunction.GetDatabaseStatusAsync(
            () => throw new InvalidOperationException("Omgevingsvariabele 'POSTGRES_CONNECTION_STRING' is niet gezet."));

        status.Should().Be("unconfigured");
        serverVersion.Should().BeNull();
    }
}
