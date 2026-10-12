using AwesomeAssertions;
using FunctionApp.Postgres.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FunctionApp.Postgres.Tests.Sync;

/// <summary>
/// #1676, Postgres-tier: zonder toestemming voor uitgaand verkeer zet de queue-verwerking de job
/// op mislukt en keert <see cref="SyncJobProcessor.Run"/> terug vóór RunConfiguredSyncAsync.
/// Geen database nodig: de opslag is een lijst.
/// </summary>
public class SyncJobProcessorUitgaandVerkeerTests
{
    [Fact]
    public async Task ZonderToestemming_WordtJobMislukt_EnGeenSync()
    {
        var vastgelegd = new List<(Guid, bool, string?)>();
        var jobId = Guid.NewGuid();

        var overgeslagen = await SyncJobProcessor.OverslaanZonderUitgaandVerkeerAsync(
            jobId, NullLogger.Instance, uitgaandVerkeerToegestaan: () => false,
            vastleggen: (id, ok, melding) => { vastgelegd.Add((id, ok, melding)); return Task.CompletedTask; });

        overgeslagen.Should().BeTrue();
        vastgelegd.Should().Equal([(jobId, false, "Uitgaande integraties zijn buiten productie uitgeschakeld.")]);
    }

    [Fact]
    public async Task MetToestemming_GaatJobDoor()
    {
        var aantalVastleggingen = 0;

        var overgeslagen = await SyncJobProcessor.OverslaanZonderUitgaandVerkeerAsync(
            Guid.NewGuid(), NullLogger.Instance, () => true,
            (_, _, _) => { aantalVastleggingen++; return Task.CompletedTask; });

        overgeslagen.Should().BeFalse();
        aantalVastleggingen.Should().Be(0);
    }
}
