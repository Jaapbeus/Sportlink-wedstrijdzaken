using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Monitoring;
using Xunit;

namespace FunctionApp.Tests.Monitoring;

/// <summary>
/// Bewijst dat de #831-registraties daadwerkelijk resolven. Mirror van de registraties in
/// <c>FunctionApp/Program.cs</c> — bij een wijziging daar moet deze test bewust worden meegenomen
/// (Program.cs is een top-level-statements-bestand zonder herbruikbare ConfigureServices-methode om
/// rechtstreeks aan te roepen).
/// </summary>
public class NoodmailThrottleDiRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<INoodmailThrottleStore>(sp =>
            new TableStorageNoodmailThrottleStore(
                "UseDevelopmentStorage=true",
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<TableStorageNoodmailThrottleStore>()));
        services.AddSingleton<IDatabaseStatusReader, ArmDatabaseStatusReader>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void INoodmailThrottleStore_Resolveert_ZonderExceptie()
    {
        using var provider = BuildProvider();

        var store = provider.GetRequiredService<INoodmailThrottleStore>();

        store.Should().BeOfType<TableStorageNoodmailThrottleStore>();
    }

    [Fact]
    public void IDatabaseStatusReader_Resolveert_ZonderExceptie()
    {
        using var provider = BuildProvider();

        var reader = provider.GetRequiredService<IDatabaseStatusReader>();

        reader.Should().BeOfType<ArmDatabaseStatusReader>();
    }

    [Fact]
    public void INoodmailThrottleStore_IsSingleton_ZelfdeInstantieBijTweedeResolutie()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<INoodmailThrottleStore>();
        var second = provider.GetRequiredService<INoodmailThrottleStore>();

        first.Should().BeSameAs(second);
    }
}
