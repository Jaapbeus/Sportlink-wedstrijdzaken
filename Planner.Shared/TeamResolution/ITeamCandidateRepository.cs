namespace Planner.Shared.TeamResolution;

/// <summary>
/// Poort naar de team- en aliastabellen voor <see cref="TeamResolver"/>. Sinds #1716 (slice 2) staat
/// alleen deze interface hier; de implementatie blijft per databasetier
/// (<c>FunctionApp/TeamResolution/TeamCandidateRepository.cs</c> tegen <c>dbo.Teams</c>/
/// <c>dbo.TeamAliassen</c>, <c>FunctionApp.Postgres/TeamResolution/TeamCandidateRepository.cs</c>
/// tegen <c>public.teams</c>/<c>public.teamaliassen</c>). Geen gedeelde providerabstractie: de tier
/// levert de databasevraag, deze kern alleen de beslisvolgorde. Een fake implementatie maakt
/// <see cref="TeamResolver"/> testbaar zonder database (<c>Planner.Shared.Tests/TeamResolution/TeamResolverTests.cs</c>).
/// </summary>
public interface ITeamCandidateRepository
{
    /// <summary>
    /// Exacte hit op een gevalideerde alias (status = 'validated'), hoogste betrouwbaarheid.
    /// Zoekt op de ruwe tekst én op de genormaliseerde sleutel: de sync registreert de exacte
    /// bronschrijfwijze, terwijl een geleerde alias juist op de genormaliseerde vorm is vastgelegd.
    /// </summary>
    Task<TeamCandidate?> FindValidatedAliasAsync(string clubCode, string ruweTekst, string genormaliseerdeSleutel);

    /// <summary>Exacte hit op de canonieke teamnaam in de teamtabel van de tier.</summary>
    Task<TeamCandidate?> FindExactTeamAsync(string clubCode, string genormaliseerdeSleutel);

    /// <summary>
    /// Kandidaten op basis van ontlede componenten (bijv. leeftijd+teamnummer zonder prefix) —
    /// kan 0, 1 of meerdere rijen opleveren. Bij precies 1 is de ambiguïteit vanzelf opgelost.
    /// </summary>
    Task<IReadOnlyList<TeamCandidate>> FindKandidatenAsync(string clubCode, TeamNaamComponenten componenten);

    /// <summary>
    /// Is er minstens één actief team bekend voor deze club? Zonder teams kan teamherkenning niet
    /// werken; zie <c>TeamlijstGereedheid</c> op de SQL Server-tier.
    /// </summary>
    Task<bool> HeeftActieveTeamsAsync(string clubCode);
}
