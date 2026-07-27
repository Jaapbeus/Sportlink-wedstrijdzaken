namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Bundelt wat de kanaal-agnostische pipeline nodig heeft om de teamnaam→ID-vertaallaag toe te
/// passen (#698/#699). Bestaat omdat <c>BerichtPipeline</c> een statische klasse is en dus geen
/// constructor-injectie heeft; de aanroepende Function haalt deze context uit DI en geeft hem mee.
///
/// Blijft de context <c>null</c>, dan gedraagt de pipeline zich exact als vóór de vertaallaag.
/// </summary>
/// <param name="Modus">Uitrolstand; bij <see cref="TeamResolverMode.Off"/> gebeurt er niets.</param>
public sealed record TeamResolutieContext(
    TeamResolverMode Modus,
    ITeamResolver Resolver,
    TeamResolutionShadowLogger ShadowLogger)
{
    /// <summary>
    /// Bouwt de context uit DI-onderdelen, of geeft <c>null</c> terug als de vertaallaag uit staat.
    /// Zo hoeft de aanroeper de stand niet zelf te interpreteren.
    /// </summary>
    public static TeamResolutieContext? Maak(ITeamResolver? resolver, TeamResolutionShadowLogger? shadowLogger)
    {
        var modus = TeamResolverModeReader.Huidig();
        if (modus == TeamResolverMode.Off || resolver is null || shadowLogger is null)
            return null;

        return new TeamResolutieContext(modus, resolver, shadowLogger);
    }
}
