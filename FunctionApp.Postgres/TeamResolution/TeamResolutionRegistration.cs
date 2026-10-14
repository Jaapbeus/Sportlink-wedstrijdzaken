using Microsoft.Extensions.DependencyInjection;

namespace FunctionApp.Postgres.TeamResolution;

/// <summary>
/// DI-registratie van de teamnaam→TeamId-vertaallaag (#1718), tegenhanger van de registratie in
/// <c>FunctionApp/Program.cs</c>. Factory-registraties: beide implementatietypen zijn <c>internal</c>.
/// De verbindingsstring wordt pas bij de eerste resolutie gelezen, zodat een ontbrekende instelling
/// de host niet bij het opstarten blokkeert.
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
