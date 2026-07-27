namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Standaardimplementatie van <see cref="ITeamResolver"/> (#692). Resolutie is 100%
/// deterministisch (geen AI) — bij ambiguïteit (meerdere kandidaten) wordt <em>geen</em>
/// gok gedaan; de aanroepende code beslist wat er met <see cref="TeamResolutionResult.Kandidaten"/>
/// gebeurt (terugvragen aan de afzender, of een forced-choice AI-disambiguatie — vervolgwerk #697).
///
/// Volgorde: (1) gevalideerde alias → (2) exacte canonieke match → (3) kandidatenzoektocht op
/// leeftijd/teamnummer zonder prefix. Bij precies 1 kandidaat in stap 3 is de ambiguïteit al
/// vanzelf opgelost (bijv. een club met alleen JO13-1, geen MO13-1).
/// </summary>
public sealed class TeamResolver(ITeamCandidateRepository repository) : ITeamResolver
{
    public async Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClubCode))
            throw new ArgumentException("ClubCode is verplicht voor teamresolutie.", nameof(request));

        var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(request.RuweTeamTekst);
        if (sleutel.Length == 0)
            return TeamResolutionResult.Onopgelost;

        var alias = await repository.FindValidatedAliasAsync(request.ClubCode, sleutel);
        if (alias is not null)
            return new TeamResolutionResult(alias.TeamId, alias.Teamnaam, 1.0, [], ResolutionBron.ExacteAlias);

        var exact = await repository.FindExactTeamAsync(request.ClubCode, sleutel);
        if (exact is not null)
            return new TeamResolutionResult(exact.TeamId, exact.Teamnaam, 1.0, [], ResolutionBron.ExacteMatch);

        var componenten = TeamNaamNormalisatie.Parse(request.RuweTeamTekst);
        if (componenten is null)
            return TeamResolutionResult.Onopgelost;

        var kandidaten = await repository.FindKandidatenAsync(request.ClubCode, componenten);
        return kandidaten.Count switch
        {
            0 => TeamResolutionResult.Onopgelost,
            1 => new TeamResolutionResult(kandidaten[0].TeamId, kandidaten[0].Teamnaam, 0.9, [], ResolutionBron.ExacteMatch),
            _ => new TeamResolutionResult(null, null, 0.0, kandidaten, ResolutionBron.MeerdereKandidaten),
        };
    }
}
