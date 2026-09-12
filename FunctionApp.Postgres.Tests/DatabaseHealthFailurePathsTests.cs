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
    /// <para>
    /// Sinds #1004 vereist een niet-lokale host (elke echte Supabase-instantie, dus ook deze
    /// synthetische testhost) daarnaast expliciet <c>sslmode=verify-full</c> — zonder die query-
    /// parameter gooit <see cref="PostgresDatabaseConfig.BuildConnectionString"/> nu bewust een
    /// <see cref="InvalidOperationException"/> (zie <see cref="BuildConnectionString_MetSupabaseUriVormZonderVerifyFull_GooitInvalidOperationException"/>
    /// hieronder). Deze test bewijst dat de #976-parsingfix zelf nog steeds werkt zodra die
    /// parameter wél aanwezig is.
    /// </para>
    /// </summary>
    [Fact]
    public void BuildConnectionString_MetSupabaseUriVorm_WerktZonderTeGooien()
    {
        var result = PostgresDatabaseConfig.BuildConnectionString(
            "postgresql://postgres.abcdefgh:wachtwoord123@aws-0-eu-central-1.pooler.supabase.com:5432/postgres?sslmode=verify-full");

        result.Should().Contain("Host=aws-0-eu-central-1.pooler.supabase.com");
        result.Should().Contain("Username=postgres.abcdefgh");
    }

    /// <summary>
    /// #1004: een niet-lokale host zonder expliciete <c>verify-full</c>-certificaatvalidatie moet
    /// bij het opstarten worden geweigerd in plaats van een onbeveiligde (MITM-zwakke) verbinding
    /// toe te staan — precies het scenario van de oorspronkelijke #976-productieconnectiestring,
    /// vóór #1004 nog altijd zonder certificaatvalidatie geaccepteerd.
    /// </summary>
    [Fact]
    public void BuildConnectionString_MetSupabaseUriVormZonderVerifyFull_GooitInvalidOperationException()
    {
        Action act = () => PostgresDatabaseConfig.BuildConnectionString(
            "postgresql://postgres.abcdefgh:wachtwoord123@aws-0-eu-central-1.pooler.supabase.com:5432/postgres");

        act.Should().Throw<InvalidOperationException>().WithMessage("*VerifyFull*");
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
