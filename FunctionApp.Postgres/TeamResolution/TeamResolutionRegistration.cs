using Microsoft.Extensions.DependencyInjection;
using Planner.Shared.TeamResolution;

namespace FunctionApp.Postgres.TeamResolution;

/// <summary>
/// DI-registratie van de teamnaam→TeamId-vertaallaag (#1718), tegenhanger van de registratie in
/// <c>FunctionApp/Program.cs</c>. Sinds #1716 (slice 2) is
/// <see cref="TeamResolver"/> de gedeelde klasse uit <c>Planner.Shared</c>; deze tier levert alleen de
/// poort via <see cref="TeamCandidateRepository"/>. Factory-registratie voor de repository: dat type is
/// <c>internal</c> en krijgt de verbindingsstring mee. De verbindingsstring wordt pas bij de eerste
/// resolutie gelezen, zodat een ontbrekende instelling de host niet bij het opstarten blokkeert
/// (getest in <c>FunctionApp.Postgres.Tests/TeamResolution/TeamResolutionRegistrationTests.cs</c>).
/// </summary>
public static class TeamResolutionRegistration
{
    public static IServiceCollection Register(IServiceCollection services)
    {
        services.AddSingleton<ITeamCandidateRepository>(
            _ => new TeamCandidateRepository(PostgresDatabaseConfig.ConnectionString));
        services.AddSingleton<ITeamResolver>(
            sp => new TeamResolver(sp.GetRequiredService<ITeamCandidateRepository>()));
        return services;
    }
}
