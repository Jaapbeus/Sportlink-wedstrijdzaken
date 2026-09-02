using Planner.Shared;

namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Toegang tot <c>dbo.Teams</c>/<c>dbo.TeamAliassen</c> voor <see cref="TeamResolver"/>.
/// Interface bestaat zodat <see cref="TeamResolver"/> unit-testbaar is zonder database
/// (zie FunctionApp.Tests/TeamResolution/TeamResolverTests.cs).
/// </summary>
public interface ITeamCandidateRepository
{
    /// <summary>
    /// Exacte hit op een gevalideerde alias (status = 'validated'), hoogste betrouwbaarheid.
    /// Zoekt op de ruwe tekst én op de genormaliseerde sleutel: de sync registreert de exacte
    /// bronschrijfwijze, terwijl een geleerde alias juist op de genormaliseerde vorm is vastgelegd.
    /// </summary>
    Task<TeamCandidate?> FindValidatedAliasAsync(string clubCode, string ruweTekst, string genormaliseerdeSleutel);

    /// <summary>Exacte hit op de canonieke teamnaam in <c>dbo.Teams</c>.</summary>
    Task<TeamCandidate?> FindExactTeamAsync(string clubCode, string genormaliseerdeSleutel);

    /// <summary>
    /// Kandidaten op basis van ontlede componenten (bijv. leeftijd+teamnummer zonder prefix) —
    /// kan 0, 1 of meerdere rijen opleveren. Bij precies 1 is de ambiguïteit vanzelf opgelost.
    /// </summary>
    Task<IReadOnlyList<TeamCandidate>> FindKandidatenAsync(string clubCode, TeamNaamComponenten componenten);

    /// <summary>
    /// Is er minstens één actief team bekend voor deze club? Zonder teams kan teamherkenning niet
    /// werken; zie <see cref="TeamlijstGereedheid"/>.
    /// </summary>
    Task<bool> HeeftActieveTeamsAsync(string clubCode);
}
