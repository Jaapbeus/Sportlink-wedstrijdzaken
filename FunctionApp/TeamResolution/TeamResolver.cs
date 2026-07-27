namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Standaardimplementatie van <see cref="ITeamResolver"/> (#692). De identiteitsbeslissing is
/// deterministisch: alleen wanneer de deterministische stappen méérdere kandidaten overhouden
/// mag een optionele <see cref="ITeamDisambiguator"/> kiezen uit die korte lijst (forced choice,
/// #697) — en de resolver valideert die keuze daarna nog tegen de kandidaten.
///
/// Volgorde: (1) gevalideerde alias → (2) exacte canonieke match → (3) kandidaten op
/// leeftijd+teamnummer → (4) bij >1 kandidaat: disambiguatie, of onbeslist teruggeven.
/// </summary>
public sealed class TeamResolver(
    ITeamCandidateRepository repository,
    ITeamDisambiguator? disambiguator = null) : ITeamResolver
{
    /// <summary>Confidence bij een keuze uit disambiguatie — bewust lager dan een exacte match.</summary>
    private const double DisambiguatieConfidence = 0.7;

    /// <summary>Confidence bij een unieke kandidaat na prefixloze zoektocht ("13-1" → precies één team).</summary>
    private const double UniekeKandidaatConfidence = 0.9;

    public async Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClubCode))
            throw new ArgumentException("ClubCode is verplicht voor teamresolutie.", nameof(request));

        // De clubprefix wordt gestript zodat de KNVB-notatie ("[club] O13-1") samenvalt met de
        // lokale notatie ("JO13-1") — zie TeamNaamNormalisatie voor de onderbouwing.
        var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(request.RuweTeamTekst, request.ClubCode);
        if (sleutel.Length == 0)
            return TeamResolutionResult.Onopgelost;

        var alias = await repository.FindValidatedAliasAsync(request.ClubCode, sleutel);
        if (alias is not null)
            return new TeamResolutionResult(alias.TeamId, alias.Teamnaam, 1.0, [], ResolutionBron.ExacteAlias);

        var exact = await repository.FindExactTeamAsync(request.ClubCode, sleutel);
        if (exact is not null)
            return new TeamResolutionResult(exact.TeamId, exact.Teamnaam, 1.0, [], ResolutionBron.ExacteMatch);

        var componenten = TeamNaamNormalisatie.Parse(request.RuweTeamTekst, request.ClubCode);
        if (componenten is null)
            return TeamResolutionResult.Onopgelost;

        var kandidaten = await repository.FindKandidatenAsync(request.ClubCode, componenten);
        switch (kandidaten.Count)
        {
            case 0:
                return TeamResolutionResult.Onopgelost;

            case 1:
                return new TeamResolutionResult(
                    kandidaten[0].TeamId, kandidaten[0].Teamnaam, UniekeKandidaatConfidence, [], ResolutionBron.ExacteMatch);

            default:
                if (disambiguator is null)
                    return new TeamResolutionResult(null, null, 0.0, kandidaten, ResolutionBron.MeerdereKandidaten);

                var gekozenId = await disambiguator.KiesAsync(request.RuweTeamTekst, kandidaten);
                var gekozen = gekozenId is null ? null : kandidaten.FirstOrDefault(k => k.TeamId == gekozenId);

                return gekozen is null
                    ? new TeamResolutionResult(null, null, 0.0, kandidaten, ResolutionBron.MeerdereKandidaten)
                    : new TeamResolutionResult(
                        gekozen.TeamId, gekozen.Teamnaam, DisambiguatieConfidence, kandidaten, ResolutionBron.AiDisambiguatie);
        }
    }
}
