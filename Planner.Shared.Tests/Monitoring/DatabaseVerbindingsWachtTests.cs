using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenPogingen_SluitenElkeVerbinding_EnSignalerenAlleenNaSucces(bool blijvendeFout)
    {
        var verbindingen = new List<ProefVerbinding>();
        var naOpen = 0;
        var actie = () => DatabaseVerbindingsWacht.WachtAsync(() =>
        {
            var verbinding = new ProefVerbinding(blijvendeFout || verbindingen.Count == 0);
            verbindingen.Add(verbinding);
            return verbinding;
        }, 2, 0, NullLogger.Instance, () => naOpen++);

        if (blijvendeFout) await actie.Should().ThrowAsync<DatabaseVerbindingOnbeschikbaarException>();
        else await actie();

        verbindingen.Should().HaveCount(2);
        verbindingen.Should().OnlyContain(v => v.OpenPogingen == 1 && v.Opgeruimd);
        naOpen.Should().Be(blijvendeFout ? 0 : 1);
    }

    private sealed class ProefVerbinding(bool faalt) : DbConnection
    {
        public int OpenPogingen { get; private set; }
        public bool Opgeruimd { get; private set; }
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => ConnectionState.Closed;
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            OpenPogingen++;
            return faalt ? Task.FromException(new TestDbException()) : Task.CompletedTask;
        }
        public override ValueTask DisposeAsync()
        {
            Opgeruimd = true;
            return ValueTask.CompletedTask;
        }
        public override void Open() => throw new NotSupportedException();
        public override void Close() => throw new NotSupportedException();
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

}
