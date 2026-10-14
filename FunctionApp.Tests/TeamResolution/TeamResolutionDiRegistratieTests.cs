using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Planner.Shared.TeamResolution;
using SportlinkFunction.TeamResolution;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

/// <summary>
/// Omhulseltest van de SQL Server-tier voor de teamresolutie (#1716 slice 2). De beslisvolgorde staat
/// in <c>Planner.Shared/TeamResolution/TeamResolver.cs</c> en is daar getest; deze tier levert alleen
/// de poort (<see cref="TeamCandidateRepository"/>) en de registratie in <c>FunctionApp/Program.cs</c>.
/// Program.cs is een top-level-statementsbestand zonder aanroepbare registratiemethode (zelfde
/// spiegelpatroon als <c>EmailPersistenceDiRegistrationTests</c>); de tekstcontrole hieronder houdt de
/// spiegel gelijk aan Program.cs.
/// </summary>
public class TeamResolutionDiRegistratieTests
{
    private const string Registratie = "builder.Services.AddSingleton<ITeamCandidateRepository, TeamCandidateRepository>();";
    private const string ResolverRegistratie = "builder.Services.AddSingleton<ITeamResolver, TeamResolver>();";

    private static ServiceProvider BouwProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITeamCandidateRepository, TeamCandidateRepository>();
        services.AddSingleton<ITeamResolver, TeamResolver>();
        return services.BuildServiceProvider();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("de testrunner moet ergens onder de repository-root draaien");
        return dir!.FullName;
    }

    [Fact]
    public void ProgramCs_RegistreertDeGedeeldeResolverMetDeTierPoort()
    {
        var bron = File.ReadAllText(Path.Combine(RepoRoot(), "FunctionApp", "Program.cs"));

        bron.Should().Contain(Registratie);
        bron.Should().Contain(ResolverRegistratie);
        bron.Should().Contain("using Planner.Shared.TeamResolution;",
            "de resolver komt uit Planner.Shared, niet uit een eigen kopie in deze tier");
    }

    [Fact]
    public void ITeamResolver_IsDeGedeeldeResolver_EnDePoortDeTierRepository()
    {
        using var provider = BouwProvider();

        provider.GetRequiredService<ITeamResolver>().Should().BeOfType<TeamResolver>();
        provider.GetRequiredService<ITeamCandidateRepository>().Should().BeOfType<TeamCandidateRepository>();
        provider.GetRequiredService<ITeamResolver>().Should().BeSameAs(provider.GetRequiredService<ITeamResolver>());
    }

    [Fact]
    public async Task GedeeldeResolver_MetTierRepository_LegeTekstZonderDatabase()
    {
        // Lege tekst wordt in de gedeelde kern afgevangen vóór de poort: geen verbinding nodig.
        using var provider = BouwProvider();

        var resultaat = await provider.GetRequiredService<ITeamResolver>()
            .ResolveAsync(new TeamResolutionRequest("  ", null, null, "TESTCLUB"));

        resultaat.Should().Be(TeamResolutionResult.Onopgelost);
    }
}
