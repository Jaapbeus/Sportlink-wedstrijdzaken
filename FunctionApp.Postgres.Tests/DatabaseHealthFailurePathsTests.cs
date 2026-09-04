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

    /// <summary>
    /// #976-incident: de eerste productiecutover zette de Azure-instelling
    /// <c>POSTGRES_CONNECTION_STRING</c> op Supabase's URI-vorm
    /// (<c>postgresql://gebruiker:wachtwoord@host:5432/database</c>) — die vorm liet
    /// <c>NpgsqlConnectionStringBuilder</c> vóór deze fix stuklopen bij het opstarten van de hele
    /// Function App (health gaf aanhoudend 503, geen cold-start-vertraging maar een echte crash).
    /// </summary>
    [Fact]
    public void BuildConnectionString_MetSupabaseUriVorm_WerktZonderTeGooien()
    {
        var result = PostgresDatabaseConfig.BuildConnectionString(
            "postgresql://postgres.abcdefgh:wachtwoord123@aws-0-eu-central-1.pooler.supabase.com:5432/postgres");

        result.Should().Contain("Host=aws-0-eu-central-1.pooler.supabase.com");
        result.Should().Contain("Username=postgres.abcdefgh");
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
