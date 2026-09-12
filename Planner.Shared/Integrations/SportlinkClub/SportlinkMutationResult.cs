namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Resultaat van een schrijvende Sportlink-aanroep (#992 e.v.). <c>IsSuccess=false</c> met
/// <see cref="SportlinkClubResponse{T}.Status"/><c>=Ok</c> betekent: de aanroep zelf is gelukt,
/// maar Sportlink heeft de mutatie zelf afgewezen (validatiefout, bijv. een niet-toegestane
/// kleedkamercombinatie) — dat is geen <c>SportlinkFout</c> (onze kant/verbinding), maar een
/// inhoudelijke weigering die de aanroeper aan de gebruiker moet tonen.
/// </summary>
public sealed record SportlinkMutationResult(
    bool IsSuccess,
    IReadOnlyList<string>? Violations);
