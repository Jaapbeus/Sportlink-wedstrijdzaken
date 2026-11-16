using System.Data.Common;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Monitoring;
using Xunit;

namespace Planner.Shared.Tests.Monitoring;

public sealed class DatabaseVerbindingsWachtTests
{
    private sealed class TestDbException() : DbException("synthetic connection failure");

    [Fact]
    public async Task Verbindingsfout_KrijgtRetriesEnAlleenVeiligGetypeerdEindresultaat()
    {
        var pogingen = 0;
        var actie = () => DatabaseVerbindingsWacht.WachtAsync(() =>
        {
            pogingen++;
            throw new TestDbException();
        }, 2, 0, NullLogger.Instance);

        var fout = await actie.Should().ThrowAsync<DatabaseVerbindingOnbeschikbaarException>();
        pogingen.Should().Be(2);
        fout.Which.Message.Should().NotContain("synthetic");
    }

    [Fact]
    public async Task KortstondigeVerbindingsfout_HersteltBinnenBestaandWachtvenster()
    {
        var pogingen = 0;
        await DatabaseVerbindingsWacht.WachtAsync(() =>
        {
            pogingen++;
            return pogingen == 1 ? Task.FromException(new TestDbException()) : Task.CompletedTask;
        }, 2, 0, NullLogger.Instance);

        pogingen.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguratieOfAnnulering_WordtGeenValseVerbindingsUitval(bool geannuleerd)
    {
        var pogingen = 0;
        var actie = () => DatabaseVerbindingsWacht.WachtAsync(() =>
        {
            pogingen++;
            return Task.FromException(geannuleerd
                ? new OperationCanceledException() : new InvalidOperationException("configuration"));
        }, 3, 0, NullLogger.Instance);

        if (geannuleerd) await actie.Should().ThrowAsync<OperationCanceledException>();
        else await actie.Should().ThrowAsync<InvalidOperationException>();
        pogingen.Should().Be(1);
    }
}
