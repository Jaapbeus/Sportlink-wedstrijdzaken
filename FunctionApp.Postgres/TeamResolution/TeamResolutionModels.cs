namespace FunctionApp.Postgres.TeamResolution;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/TeamResolution/TeamResolutionModels.cs</c> (#889).
/// <c>TeamCandidate</c> zelf staat NIET hier — die bestond al in <c>TeamCandidateRepository.cs</c>
/// (identieke vorm, #887) vóórdat deze vertaling begon; een tweede definitie zou een
/// duplicate-type-compilefout geven.
/// </summary>
/// <param name="RuweTeamTekst">Bijv. "JO13-2", "13-1", "Onder 13 1" — zoals door de AI/afzender geleverd.</param>
/// <param name="RuweLeeftijdsHint">Optionele losse leeftijdscategorie-hint van de AI, indien apart geleverd.</param>
/// <param name="IsWaarschijnlijkEigenTeam">
/// AI-signaal (bijv. afgeleid van <c>namensWie</c>) — vervangt op termijn de fragiele
/// "geen spatie"/"clubcode-prefix"-heuristiek in <c>BerichtPipeline.VerwerkMetPlannerAsync</c>.
/// </param>
internal sealed record TeamResolutionRequest(
    string RuweTeamTekst,
    string? RuweLeeftijdsHint,
    bool? IsWaarschijnlijkEigenTeam,
    string ClubCode);

/// <summary>Waar de resolutie-uitkomst vandaan kwam — bepaalt hoe zeker de uitkomst is.</summary>
internal enum ResolutionBron
{
    /// <summary>Exacte hit op een gevalideerde alias in <c>public.teamaliassen</c>.</summary>
    ExacteAlias,

    /// <summary>Exacte hit op de canonieke teamnaam in <c>public.teams</c>.</summary>
    ExacteMatch,

    /// <summary>Meerdere kandidaten gevonden en géén betrouwbare keuze gemaakt — terugvragen aan de afzender.</summary>
    MeerdereKandidaten,

    /// <summary>Keuze uit een korte kandidatenlijst gemaakt door een AI-disambiguator — niet vertaald op deze tier.</summary>
    AiDisambiguatie,

    /// <summary>Geen enkele kandidaat gevonden.</summary>
    Onopgelost,
}

/// <summary>
/// Uitkomst van <see cref="ITeamResolver.ResolveAsync"/>. <c>TeamId</c> is alleen gezet bij een
/// eenduidige uitkomst. Bij <see cref="ResolutionBron.MeerdereKandidaten"/> bevat
/// <c>Kandidaten</c> de mogelijke teams, zodat de aanroeper de vraag kan terugleggen bij de
/// afzender in plaats van te gokken.
/// </summary>
internal sealed record TeamResolutionResult(
    int? TeamId,
    string? CanoniekeTeamnaam,
    double Confidence,
    IReadOnlyList<TeamCandidate> Kandidaten,
    ResolutionBron Bron)
{
    public static readonly TeamResolutionResult Onopgelost =
        new(null, null, 0.0, [], ResolutionBron.Onopgelost);

    /// <summary>True als er precies één team is vastgesteld.</summary>
    public bool IsOpgelost => TeamId is not null && !string.IsNullOrWhiteSpace(CanoniekeTeamnaam);
}
