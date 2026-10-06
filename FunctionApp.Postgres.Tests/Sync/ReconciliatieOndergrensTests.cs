using AwesomeAssertions;
using FunctionApp.Postgres.Sync;
using Xunit;

namespace FunctionApp.Postgres.Tests.Sync;

/// <summary>
/// Review #1547 R1-F2: afwezigheid in de sync mag een wedstrijd pas vanaf morgen als verwijderd
/// markeren — op de speeldag zelf kan een gespeelde wedstrijd uit /programma verdwenen zijn terwijl
/// de uitslag nog niet gepubliceerd is.
/// </summary>
public class ReconciliatieOndergrensTests
{
    [Fact]
    public void Ondergrens_IsMorgen_NooitVandaag()
    {
        var zaterdag = new DateOnly(2026, 10, 10);

        PostgresSyncPipeline.EersteTeReconcilierenDatum(zaterdag).Should().Be(new DateOnly(2026, 10, 11));
    }
}
