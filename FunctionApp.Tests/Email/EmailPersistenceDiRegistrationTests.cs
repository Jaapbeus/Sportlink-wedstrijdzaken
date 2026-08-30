using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Bewijst dat de #827-registraties daadwerkelijk resolven zonder dat productiecode zelf een `new`
/// hoeft te doen. Mirror van de registraties in <c>FunctionApp/Program.cs</c> — bij een wijziging
/// daar moet deze test bewust worden meegenomen (Program.cs is een top-level-statements-bestand
/// zonder herbruikbare ConfigureServices-methode om rechtstreeks aan te roepen).
/// </summary>
public class EmailPersistenceDiRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmailPersistenceRepository>(_ => new SqlEmailPersistenceRepository());
        services.AddSingleton<IEmailPersistenceService>(sp =>
            new EmailPersistenceService(sp.GetRequiredService<IEmailPersistenceRepository>()));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void IEmailPersistenceRepository_Resolveert_ZonderExceptie()
    {
        using var provider = BuildProvider();

        var repository = provider.GetRequiredService<IEmailPersistenceRepository>();

        repository.Should().BeOfType<SqlEmailPersistenceRepository>();
    }

    [Fact]
    public void IEmailPersistenceService_Resolveert_MetGeinjecteerdeRepository()
    {
        using var provider = BuildProvider();

        var service = provider.GetRequiredService<IEmailPersistenceService>();

        service.Should().BeOfType<EmailPersistenceService>();
    }

    [Fact]
    public void IEmailPersistenceRepository_IsSingleton_ZelfdeInstantieBijTweedeResolutie()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<IEmailPersistenceRepository>();
        var second = provider.GetRequiredService<IEmailPersistenceRepository>();

        first.Should().BeSameAs(second);
    }
}
