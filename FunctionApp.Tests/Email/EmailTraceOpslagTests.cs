using AwesomeAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email.Trace;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>De trace is een hulpmiddel: opslag mag de e-mailverwerking nooit laten falen (#1568, deel B).</summary>
public class EmailTraceOpslagTests
{
    private static BeslissingsTrace Trace() => new TraceBuilder().Classificatie("Overig", false, false, 0, false).Bouw();

    [Fact]
    public async Task BewaarTraceVeilig_SlaatDeTraceOp()
    {
        var service = new RecordingEmailPersistenceService();

        await EmailProcessorFunction.BewaarTraceVeiligAsync(service, 42, "Overig", Trace(), NullLogger.Instance);

        service.Traces.Should().ContainSingle(t => t.VerwerkingId == 42 && t.VerzoekType == "Overig");
    }

    [Fact]
    public async Task BewaarTraceVeilig_GooitNiet_AlsOpslagFaalt()
    {
        var service = new RecordingEmailPersistenceService { TraceFout = new InvalidOperationException("db weg") };

        var act = () => EmailProcessorFunction.BewaarTraceVeiligAsync(service, 42, "Overig", Trace(), NullLogger.Instance);

        await act.Should().NotThrowAsync();
        service.Traces.Should().BeEmpty();
    }

    [Fact]
    public async Task BewaarTraceVeilig_GeeftDeRepositoryGeenRuweTeamtekst()
    {
        var repo = new FakeEmailPersistenceRepository();
        var service = new EmailPersistenceService(repo, () => "ALLSTARS");
        var trace = new TraceBuilder().Classificatie("BeschikbaarheidCheck", true, true, 1, true)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "Pieter komt zaterdag niet vanwege de regen", "Onopgelost", 0, null, null)
            .Bouw();

        await EmailProcessorFunction.BewaarTraceVeiligAsync(service, 42, "BeschikbaarheidCheck", trace, NullLogger.Instance);

        var record = repo.Traces.Should().ContainSingle().Subject;
        record.TraceJson.Should().NotContain("Pieter").And.NotContain("regen").And.NotContain("ruweTekst");
    }
}
