using Planner.Shared;

namespace FunctionApp.Postgres.TeamResolution;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/TeamResolution/TeamResolver.cs</c> (#889) —
/// woordelijke kopie. Standaardimplementatie van <see cref="ITeamResolver"/>: het enige pad
/// waarlangs een teamaanduiding uit vrije tekst aan een team wordt gekoppeld.
///
/// <para>
/// De identiteitsbeslissing is volledig deterministisch: meerdere kandidaten leveren altijd
/// <c>MeerdereKandidaten</c> op, met de lijst erbij. Geen gok, geen stille aanname. Er wordt vanuit
/// deze klasse dus ook geen alias geleerd — dat gebeurde alleen ná een AI-keuze.
/// <para>
/// Dit gedrag is sinds #1268 identiek op beide tiers. De SQL Server-tier had tot dan een
/// AI-disambiguator (#697) die deze tier nooit heeft gehad; dat verschil is opgeheven door de
/// deterministische kant als norm te nemen.
/// </para>
///
/// Volgorde: (1) gevalideerde alias → (2) exacte canonieke match → (3) kandidaten op
/// leeftijd+teamnummer → (4) bij >1 kandidaat: onbeslist teruggeven (geen disambiguatie op deze tier).
/// </summary>
internal sealed class TeamResolver(ITeamCandidateRepository repository) : ITeamResolver
{
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
                return Onbeslist(kandidaten);
        }
    }

    private static TeamResolutionResult Onbeslist(IReadOnlyList<TeamCandidate> kandidaten)
        => new(null, null, 0.0, kandidaten, ResolutionBron.MeerdereKandidaten);

    private static TeamResolutionResult Opgelost(
        TeamCandidate team, double confidence, IReadOnlyList<TeamCandidate> kandidaten, ResolutionBron bron)
        => new(team.TeamId, team.Teamnaam, confidence, kandidaten, bron);
}
