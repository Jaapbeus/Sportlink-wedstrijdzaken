using AwesomeAssertions;
using FunctionApp.Postgres.TeamResolution;
using Microsoft.Extensions.DependencyInjection;
using Planner.Shared;
using Planner.Shared.TeamResolution;
using Xunit;

namespace FunctionApp.Postgres.Tests.TeamResolution;

/// <summary>
/// Omhulseltest van de Postgres-tier voor de teamresolutie (#1716 slice 2). De beslisvolgorde staat in
/// <c>Planner.Shared/TeamResolution/TeamResolver.cs</c> en is daar getest; deze tier levert alleen de
/// poort (<c>TeamCandidateRepository</c>) en <see cref="TeamResolutionRegistration"/>. Zonder database:
/// de repositoryfactory wordt niet aangeroepen (die leest de verbindingsstring), de resolver krijgt
/// in de koppelingstest een vervangende poort.
/// </summary>
public class TeamResolutionRegistrationTests
{
    [Fact]
    public void Register_BeideDienstenAlsSingletonMetFactory()
    {
        var services = TeamResolutionRegistration.Register(new ServiceCollection());

        var poort = services.Single(d => d.ServiceType == typeof(ITeamCandidateRepository));
        var resolver = services.Single(d => d.ServiceType == typeof(ITeamResolver));
        poort.Lifetime.Should().Be(ServiceLifetime.Singleton);
        poort.ImplementationFactory.Should().NotBeNull("de verbindingsstring wordt pas bij de eerste resolutie gelezen");
        resolver.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public async Task ITeamResolver_IsDeGedeeldeResolver_DieDeGeregistreerdePoortGebruikt()
    {
        var services = TeamResolutionRegistration.Register(new ServiceCollection());
        services.AddSingleton<ITeamCandidateRepository>(new VastePoort());
        using var provider = services.BuildServiceProvider();

        var resolver = provider.GetRequiredService<ITeamResolver>();
        var resultaat = await resolver.ResolveAsync(new TeamResolutionRequest("JO13-1", null, null, "TESTCLUB"));

        resolver.Should().BeOfType<TeamResolver>();
        resultaat.TeamId.Should().Be(7);
        resultaat.Bron.Should().Be(ResolutionBron.ExacteMatch);
    }

    private sealed class VastePoort : ITeamCandidateRepository
    {
        public Task<TeamCandidate?> FindValidatedAliasAsync(string clubCode, string ruweTekst, string genormaliseerdeSleutel)
            => Task.FromResult<TeamCandidate?>(null);

        public Task<TeamCandidate?> FindExactTeamAsync(string clubCode, string genormaliseerdeSleutel)
            => Task.FromResult<TeamCandidate?>(new TeamCandidate(7, "TESTCLUB O13-1", "JO13"));

        public Task<IReadOnlyList<TeamCandidate>> FindKandidatenAsync(string clubCode, TeamNaamComponenten componenten)
            => Task.FromResult<IReadOnlyList<TeamCandidate>>([]);

        public Task<bool> HeeftActieveTeamsAsync(string clubCode) => Task.FromResult(true);
    }
}
