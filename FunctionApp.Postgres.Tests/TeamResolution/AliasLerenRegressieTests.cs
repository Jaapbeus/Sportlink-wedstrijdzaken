using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Leren;
using Planner.Shared;
using Planner.Shared.Leren;
using FunctionApp.Postgres.TeamResolution;
using Xunit;

namespace FunctionApp.Postgres.Tests.TeamResolution;

/// <summary>
/// Regressietest van het leren vanuit de trace (#1568 deel C): na het aanmaken van een alias door een
/// beheerder lost <see cref="TeamResolver"/> diezelfde tekst op als <c>ExacteAlias</c> met zekerheid 1.0.
/// De opslag is een in-memory wereld die doet wat de Postgres-store doet (alias <c>validated</c>, sleutel uit
/// <see cref="TeamNaamNormalisatie"/>) — de echte Postgres-queries zijn databasegebonden.
/// </summary>
public class AliasLerenRegressieTests
{
    private const string Club = "TESTCLUB";
    private static readonly LerenAanroeper Wie = new("oid-1", "Testbeheerder");

    [Fact]
    public async Task NaHetAanmakenVanDeAlias_LostDeResolverDeTekstOpAlsExacteAlias()
    {
        var wereld = new AliasWereld(new TeamCandidate(4, "TESTCLUB O10-4", "JO10"));
        var resolver = new TeamResolver(wereld);

        var voor = await resolver.ResolveAsync(new TeamResolutionRequest("j10-04", null, null, Club));
        voor.IsOpgelost.Should().BeFalse("zonder alias is 'j10-04' niet te koppelen");

        var antwoord = await TeamAliasEndpointCore.AanmakenAsync(
            Club, "{\"ruweTekst\":\"j10-04\",\"teamId\":4}", Wie, wereld, new LegeWachtrij(), NullLogger.Instance);
        antwoord.Should().BeAssignableTo<Microsoft.AspNetCore.Mvc.ObjectResult>().Which.StatusCode.Should().Be(201);

        var na = await resolver.ResolveAsync(new TeamResolutionRequest("j10-04", null, null, Club));
        na.TeamId.Should().Be(4);
        na.CanoniekeTeamnaam.Should().Be("TESTCLUB O10-4");
        na.Bron.Should().Be(ResolutionBron.ExacteAlias);
        na.Confidence.Should().Be(1.0);

        // Een andere schrijfwijze met dezelfde genormaliseerde sleutel profiteert mee.
        (await resolver.ResolveAsync(new TeamResolutionRequest("J10-04", null, null, Club))).Bron.Should().Be(ResolutionBron.ExacteAlias);
    }

    private sealed class LegeWachtrij : IOnbekendeTeamTekstStore
    {
        public Task VoegToeAsync(string clubCode, string genormaliseerd, string voorbeeld, int verwerkingId) => Task.CompletedTask;
        public Task<IReadOnlyList<OnbekendeTeamTekstRij>> LijstAsync(string clubCode, string? status, int limit)
            => Task.FromResult<IReadOnlyList<OnbekendeTeamTekstRij>>([]);
        public Task<int> AantalOpenAsync(string clubCode) => Task.FromResult(0);
        public Task<int> ZetStatusAsync(string clubCode, int id, string status) => Task.FromResult(0);
        public Task<int> MarkeerAfgehandeldAsync(string clubCode, string genormaliseerd) => Task.FromResult(0);
    }

    private sealed class AliasWereld(TeamCandidate team) : ITeamAliasStore, ITeamCandidateRepository
    {
        private readonly List<(string Ruw, string Sleutel, int TeamId, string Status)> _aliassen = new();

        public Task<AliasAanmaakUitkomst> MaakAanAsync(AliasAanmaakOpdracht o)
        {
            if (o.TeamId != team.TeamId) return Task.FromResult(new AliasAanmaakUitkomst(AliasAanmaakStatus.TeamOnbekend));
            _aliassen.Add((o.RuweTekst, o.Genormaliseerd, o.TeamId, "validated"));
            return Task.FromResult(new AliasAanmaakUitkomst(AliasAanmaakStatus.Aangemaakt, _aliassen.Count, team.Teamnaam));
        }

        public Task<TeamCandidate?> FindValidatedAliasAsync(string clubCode, string ruweTekst, string genormaliseerdeSleutel)
            => Task.FromResult(_aliassen.Any(a => a.Status == "validated"
                    && (string.Equals(a.Ruw, ruweTekst, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a.Sleutel, genormaliseerdeSleutel, StringComparison.OrdinalIgnoreCase)))
                ? team : null);

        public Task<TeamCandidate?> FindExactTeamAsync(string clubCode, string genormaliseerdeSleutel) => Task.FromResult<TeamCandidate?>(null);
        public Task<IReadOnlyList<TeamCandidate>> FindKandidatenAsync(string clubCode, TeamNaamComponenten componenten)
            => Task.FromResult<IReadOnlyList<TeamCandidate>>([]);
        public Task<bool> HeeftActieveTeamsAsync(string clubCode) => Task.FromResult(true);
    }
}
