using System.Reflection;
using AwesomeAssertions;
using Planner.Shared.Email.Trace;
using Xunit;

namespace Planner.Shared.Tests.Email.Trace;

/// <summary>Legt het opslagrecord, de afgeleide kolommen en de "opslag faalt stil"-garantie vast (#1568, deel B).</summary>
public class EmailTraceOpslagTests
{
    private static BeslissingsTrace ZekereTrace()
        => new TraceBuilder()
            .Classificatie("BeschikbaarheidCheck", true, true, 1, true)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO13-1", "Exact", 1.0, new[] { "JO13-1" }, "JO13-1")
            .Antwoordkeuze("BeschikbaarheidCheck", """{"gewensteDatum":"x","beschikbaarheid":[]}""")
            .Bouw();

    [Fact]
    public void Van_LeidtZekerheidEnSjabloonAfUitDeTrace()
    {
        var record = EmailTraceRecord.Van(7, "ALLSTARS", "BeschikbaarheidCheck", ZekereTrace(), "1.2.3.4");

        record.VerwerkingId.Should().Be(7);
        record.ClubCode.Should().Be("ALLSTARS");
        record.Zekerheid.Should().Be("Zeker");
        record.SjabloonSleutel.Should().NotBeNullOrEmpty();
        BeslissingsTrace.FromJson(record.TraceJson).Should().NotBeNull();
    }

    [Fact]
    public void Van_KaptTekstkolommenAfOpDeKolomlengte()
    {
        var record = EmailTraceRecord.Van(1, "ALLSTARS", new string('t', 200), ZekereTrace(), new string('9', 99));

        record.VerzoekType.Length.Should().Be(EmailTraceRecord.MaxVerzoekTypeLengte);
        record.AppVersie.Length.Should().Be(EmailTraceRecord.MaxAppVersieLengte);
    }

    [Fact]
    public void ZekerheidVan_OnzekerAlsEenTeamNietIsHerkend()
    {
        var trace = new TraceBuilder()
            .Classificatie("BeschikbaarheidCheck", true, false, 1, true)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO13", "MeerdereKandidaten", 0.5, new[] { "A", "B" }, null)
            .Bouw();

        EmailTraceRecord.ZekerheidVan(trace).Should().Be("Onzeker");
    }

    [Fact]
    public void ZekerheidVan_MislukAlsEenAndereStapMislukte()
    {
        var builder = new TraceBuilder().Classificatie("Overig", false, false, 0, false);
        builder.Voeg(TraceCodes.VerwerkingFout, "Verwerking", "Mislukt", ZekerheidsNiveau.Mislukt);

        EmailTraceRecord.ZekerheidVan(builder.Bouw()).Should().Be("Mislukt");
    }

    [Fact]
    public void VersieVan_GeeftAssemblyversie()
        => EmailTraceRecord.VersieVan(Assembly.GetExecutingAssembly()).Should().NotBeNullOrWhiteSpace();

    [Fact]
    public void KorteTrace_BevatClassificatieEnReden_ZonderVrijeTekst()
    {
        var trace = EmailTraceOpslag.BouwKorteTrace("BuitenScope", null, null, 0, null, "Buiten scope");

        trace.Stappen.Select(s => s.Code).Should().Contain(new[] { TraceCodes.Classificatie, TraceCodes.Tak, TraceCodes.Eindoordeel });
        trace.ToJson().Should().Contain("Buiten scope");
    }

    [Fact]
    public async Task BewaarVeilig_GooitNietAlsOpslagFaalt_EnMeldtHetFouttype()
    {
        Exception? gemeld = null;

        var gelukt = await EmailTraceOpslag.BewaarVeiligAsync(
            () => throw new InvalidOperationException("db weg"), ex => gemeld = ex);

        gelukt.Should().BeFalse();
        gemeld.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task BewaarVeilig_GooitNietAlsDeMeldingZelfFaalt()
    {
        var gelukt = await EmailTraceOpslag.BewaarVeiligAsync(
            () => throw new InvalidOperationException(), _ => throw new InvalidOperationException());

        gelukt.Should().BeFalse();
    }

    [Fact]
    public async Task BewaarVeilig_MeldtSuccesAlsOpslagSlaagt()
    {
        var aangeroepen = false;

        var gelukt = await EmailTraceOpslag.BewaarVeiligAsync(() => { aangeroepen = true; return Task.CompletedTask; }, _ => { });

        gelukt.Should().BeTrue();
        aangeroepen.Should().BeTrue();
    }

    [Fact]
    public async Task MetTraceAsync_BewaartAltijd_EnGeeftDeVerwerkingsfoutDoor()
    {
        var trace = new TraceBuilder().Classificatie("Overig", false, false, 0, false);
        BeslissingsTrace? bewaard = null;

        var act = () => EmailTraceOpslag.MetTraceAsync<int>(trace,
            () => throw new InvalidOperationException("verwerking stuk"),
            t => { bewaard = t; return Task.CompletedTask; },
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, 5);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("verwerking stuk");
        bewaard.Should().NotBeNull();
        bewaard!.Stappen.Should().Contain(s => s.Code == TraceCodes.VerwerkingFout && s.Zekerheid == ZekerheidsNiveau.Mislukt);
    }

    [Fact]
    public async Task MetTraceAsync_FaalOpslagMaaktDeVerwerkingNietKapot()
    {
        var trace = new TraceBuilder().Classificatie("Overig", false, false, 0, false);

        var uitkomst = await EmailTraceOpslag.MetTraceAsync(trace,
            () => Task.FromResult(42),
            _ => throw new InvalidOperationException("opslag stuk"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, 5);

        uitkomst.Should().Be(42);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{kapot")]
    public void ParseTrace_GeeftNullBijOnbruikbareJson(string? json)
        => EmailTraceAntwoord.ParseTrace(json).Should().BeNull();

    [Fact]
    public void ParseTrace_LeestGeldigeJson()
        => EmailTraceAntwoord.ParseTrace(ZekereTrace().ToJson())!.Value.GetProperty("stappen").GetArrayLength().Should().BeGreaterThan(0);
}
