namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Standaardimplementatie van <see cref="ITeamResolver"/> (#692). Sinds #700 is dit het ENIGE pad
/// waarlangs een teamaanduiding uit vrije tekst aan een team wordt gekoppeld — de oude
/// regex-normalisatie en stringheuristieken zijn verwijderd.
///
/// <para>
/// De identiteitsbeslissing is deterministisch. Alleen wanneer de deterministische stappen méérdere
/// kandidaten overhouden mag een optionele <see cref="ITeamDisambiguator"/> kiezen uit die korte
/// lijst (forced choice, #697), en die keuze wordt daarna nog gevalideerd tegen de kandidaten.
/// </para>
///
/// Volgorde: (1) gevalideerde alias → (2) exacte canonieke match → (3) kandidaten op
/// leeftijd+teamnummer → (4) bij >1 kandidaat: disambiguatie, of onbeslist teruggeven.
/// </summary>
public sealed class TeamResolver(
    ITeamCandidateRepository repository,
    ITeamDisambiguator? disambiguator = null,
    TeamAliasLearningService? aliasLearning = null) : ITeamResolver
{
    /// <summary>Confidence bij een keuze uit disambiguatie — bewust lager dan een exacte match.</summary>
    private const double DisambiguatieConfidence = 0.7;

    /// <summary>Confidence bij een unieke kandidaat na prefixloze zoektocht ("13-1" → precies één team).</summary>
    private const double UniekeKandidaatConfidence = 0.9;

    public async Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClubCode))
            throw new ArgumentException("ClubCode is verplicht voor teamresolutie.", nameof(request));

        var ruweTekst = (request.RuweTeamTekst ?? "").Trim();

        // De clubprefix wordt gestript zodat de KNVB-notatie ("[club] O13-1") samenvalt met de
        // lokale notatie ("JO13-1") — zie TeamNaamNormalisatie voor de onderbouwing.
        var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(ruweTekst, request.ClubCode);
        if (sleutel.Length == 0)
            return TeamResolutionResult.Onopgelost;

        var alias = await repository.FindValidatedAliasAsync(request.ClubCode, ruweTekst, sleutel);
        if (alias is not null)
            return Opgelost(alias, 1.0, [], ResolutionBron.ExacteAlias);

        var exact = await repository.FindExactTeamAsync(request.ClubCode, sleutel);
        if (exact is not null)
            return Opgelost(exact, 1.0, [], ResolutionBron.ExacteMatch);

        var componenten = TeamNaamNormalisatie.Parse(ruweTekst, request.ClubCode);
        if (componenten is null)
            return TeamResolutionResult.Onopgelost;

        var kandidaten = await repository.FindKandidatenAsync(request.ClubCode, componenten);
        switch (kandidaten.Count)
        {
            case 0:
                return TeamResolutionResult.Onopgelost;

            case 1:
                return Opgelost(kandidaten[0], UniekeKandidaatConfidence, [], ResolutionBron.ExacteMatch);

            default:
                if (disambiguator is null)
                    return Onbeslist(kandidaten);

                var gekozenId = await disambiguator.KiesAsync(ruweTekst, kandidaten);
                var gekozen = gekozenId is null ? null : kandidaten.FirstOrDefault(k => k.TeamId == gekozenId);

                if (gekozen is null) return Onbeslist(kandidaten);

                // Leg de keuze vast als 'pending' alias. Zonder dit wordt voor élke terugkerende
                // afwijkende schrijfwijze opnieuw een AI-call betaald, blijft de keuze
                // niet-deterministisch, en ziet de coördinator op de aliassenpagina nooit iets staan.
                // Pas na goedkeuring wordt de alias vertrouwd, dus dit kan zich niet zelfversterken.
                if (aliasLearning is not null)
                    await aliasLearning.LegVastAsync(request.ClubCode, ruweTekst, gekozen.TeamId, "AiDisambiguatie");

                return Opgelost(gekozen, DisambiguatieConfidence, kandidaten, ResolutionBron.AiDisambiguatie);
        }
    }

    private static TeamResolutionResult Onbeslist(IReadOnlyList<TeamCandidate> kandidaten)
        => new(null, null, 0.0, kandidaten, ResolutionBron.MeerdereKandidaten);

    private static TeamResolutionResult Opgelost(
        TeamCandidate team, double confidence, IReadOnlyList<TeamCandidate> kandidaten, ResolutionBron bron)
        => new(team.TeamId, team.Teamnaam, confidence, kandidaten, bron);
}
