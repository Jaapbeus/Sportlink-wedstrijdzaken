using Planner.Shared;

namespace FunctionApp.Postgres.TeamResolution;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/TeamResolution/ITeamCandidateRepository.cs</c>
/// (#889). Toegang tot <c>public.teams</c>/<c>public.teamaliassen</c> voor <see cref="TeamResolver"/>.
/// <see cref="TeamCandidateRepository"/> (#887) had deze vier methoden al met exact deze signatuur —
/// deze interface formaliseert dat contract pas nu, zodat <c>TeamResolver</c> hier tegen kan
/// programmeren zoals op de SQL Server-tier.
/// </summary>
internal interface ITeamCandidateRepository
{
    /// <summary>
    /// Exacte hit op een gevalideerde alias (status = 'validated'), hoogste betrouwbaarheid.
    /// Zoekt op de ruwe tekst én op de genormaliseerde sleutel: de sync registreert de exacte
    /// bronschrijfwijze, terwijl een geleerde alias juist op de genormaliseerde vorm is vastgelegd.
    /// </summary>
    Task<TeamCandidate?> FindValidatedAliasAsync(string clubCode, string ruweTekst, string genormaliseerdeSleutel);

    /// <summary>Exacte hit op de canonieke teamnaam in <c>public.teams</c>.</summary>
    Task<TeamCandidate?> FindExactTeamAsync(string clubCode, string genormaliseerdeSleutel);

    /// <summary>
    /// Kandidaten op basis van ontlede componenten (bijv. leeftijd+teamnummer zonder prefix) —
    /// kan 0, 1 of meerdere rijen opleveren. Bij precies 1 is de ambiguïteit vanzelf opgelost.
    /// </summary>
    Task<IReadOnlyList<TeamCandidate>> FindKandidatenAsync(string clubCode, TeamNaamComponenten componenten);

    /// <summary>Is er minstens één actief team bekend voor deze club? Zonder teams kan teamherkenning niet werken.</summary>
    Task<bool> HeeftActieveTeamsAsync(string clubCode);
}
