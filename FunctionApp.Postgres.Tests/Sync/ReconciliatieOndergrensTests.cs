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

    [Fact]
    public void VandaagInNederland_GebruiktNederlandseDatum_OokVlakNaMiddernacht()
    {
        // 22:30 UTC op 10 okt = 00:30 CEST op 11 okt.
        var utc = new DateTime(2026, 10, 10, 22, 30, 0, DateTimeKind.Utc);

        PostgresSyncPipeline.VandaagInNederland(utc, TimeZoneInfo.FindSystemTimeZoneById)
            .Should().Be(new DateOnly(2026, 10, 11));
    }

    [Fact]
    public void VandaagInNederland_ValtTerugOpDeIanaId_AlsDeWindowsIdOntbreekt()
    {
        var utc = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var gevraagd = new List<string>();

        var vandaag = PostgresSyncPipeline.VandaagInNederland(utc, id =>
        {
            gevraagd.Add(id);
            if (id == "W. Europe Standard Time") throw new TimeZoneNotFoundException();
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        });

        vandaag.Should().Be(new DateOnly(2026, 1, 15));
        gevraagd.Should().Equal("W. Europe Standard Time", "Europe/Amsterdam");
    }

    [Fact]
    public void VandaagInNederland_GooitNooit_EnKiestDeVeiligeKant_ZonderTijdzonegegevens()
    {
        var utc = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

        var vandaag = PostgresSyncPipeline.VandaagInNederland(utc, _ => throw new TimeZoneNotFoundException());

        // Later dan de echte Nederlandse datum (10 okt): een latere ondergrens reconcilieert minder.
        vandaag.Should().Be(new DateOnly(2026, 10, 11));
        vandaag.Should().BeOnOrAfter(new DateOnly(2026, 10, 10));
    }
}
