namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Uitrolstand van de teamnaam→ID-vertaallaag (#698/#699). Bewust een app setting en géén
/// DB-kolom: dat is het bestaande patroon voor gedragsschakelaars in dit project
/// (<c>EmailProcessorEnabled</c>, <c>EmailReviewMode</c>) en één deployment bedient per definitie
/// één club, dus een app setting ís hier club-scoped.
/// </summary>
public enum TeamResolverMode
{
    /// <summary>Vertaallaag doet niets. Standaard — de bestaande matching blijft ongewijzigd leidend.</summary>
    Off,

    /// <summary>Vertaallaag draait mee en logt alleen of ze tot dezelfde uitkomst komt (#698).</summary>
    Shadow,

    /// <summary>Vertaallaag is leidend voor het zoeken van de wedstrijd (#699).</summary>
    On,
}

public static class TeamResolverModeReader
{
    public const string SettingName = "TeamResolverMode";

    /// <summary>
    /// Leest de stand uit de app settings. Onbekende of ontbrekende waarde → <see cref="TeamResolverMode.Off"/>:
    /// een typefout in de configuratie mag nooit stilzwijgend nieuw gedrag activeren.
    /// </summary>
    public static TeamResolverMode Huidig()
        => Lees(Environment.GetEnvironmentVariable(SettingName));

    internal static TeamResolverMode Lees(string? waarde) => waarde?.Trim().ToLowerInvariant() switch
    {
        "shadow" => TeamResolverMode.Shadow,
        "on" or "true" or "1" => TeamResolverMode.On,
        _ => TeamResolverMode.Off,
    };
}
