namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Input voor <see cref="ITeamResolver"/>: de ruwe, door de AI-classificatiestap geëxtraheerde
/// tekst (nog geen canonieke identiteit) plus de club-context waarbinnen resolutie plaatsvindt.
/// </summary>
/// <param name="RuweTeamTekst">Bijv. "JO13-2", "13-1", "Onder 13 1" — zoals door de AI/afzender geleverd.</param>
/// <param name="RuweLeeftijdsHint">Optionele losse leeftijdscategorie-hint van de AI, indien apart geleverd.</param>
/// <param name="IsWaarschijnlijkEigenTeam">
/// AI-signaal (bijv. afgeleid van <c>namensWie</c>) — vervangt op termijn de fragiele
/// "geen spatie"/"clubcode-prefix"-heuristiek in <c>BerichtPipeline.VerwerkMetPlannerAsync</c>.
/// </param>
public sealed record TeamResolutionRequest(
    string RuweTeamTekst,
    string? RuweLeeftijdsHint,
    bool? IsWaarschijnlijkEigenTeam,
    string ClubCode);

/// <summary>Eén mogelijk team uit <c>dbo.Teams</c>, gebruikt als disambiguatie-kandidaat.</summary>
public sealed record TeamCandidate(int TeamId, string Teamnaam, string? LeeftijdsCategorie);

/// <summary>Waar de resolutie-uitkomst vandaan kwam — bepaalt hoe zeker de uitkomst is.</summary>
public enum ResolutionBron
{
    /// <summary>Exacte hit op een gevalideerde alias in <c>dbo.TeamAliassen</c>.</summary>
    ExacteAlias,

    /// <summary>Exacte hit op de canonieke teamnaam in <c>dbo.Teams</c>.</summary>
    ExacteMatch,

    /// <summary>Meerdere kandidaten gevonden en géén betrouwbare keuze gemaakt — terugvragen aan de afzender.</summary>
    MeerdereKandidaten,

    /// <summary>Keuze uit een korte kandidatenlijst gemaakt door <see cref="ITeamDisambiguator"/> (#697).</summary>
    AiDisambiguatie,

    /// <summary>Geen enkele kandidaat gevonden.</summary>
    Onopgelost,
}

/// <summary>
/// Uitkomst van <see cref="ITeamResolver.ResolveAsync"/>. <see cref="TeamId"/> is alleen gezet bij een
/// eenduidige uitkomst. Bij <see cref="ResolutionBron.MeerdereKandidaten"/> bevat
/// <see cref="Kandidaten"/> de mogelijke teams, zodat de aanroeper de vraag kan terugleggen bij de
/// afzender in plaats van te gokken.
/// </summary>
public sealed record TeamResolutionResult(
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
