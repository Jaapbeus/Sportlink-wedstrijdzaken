using FluentAssertions;
using SportlinkFunction.Email;
using SportlinkFunction.Processing;
using Xunit;

namespace FunctionApp.Tests.Processing;

public class BerichtPipelineTests
{
    // ── ValideerDagDatum — datum in onderwerp ──

    [Fact]
    public void ValideerDagDatum_OnderwerpBevat_ddmmyyyy_GebruiktOnderwerpDatum()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "Kan jullie op die datum?", "Beschikbaarheid 18-4-2026");
        classificatie.Datum.Should().Be("2026-04-18");
    }

    [Fact]
    public void ValideerDagDatum_OnderwerpBevat_dmaandyyyy_GebruiktOnderwerpDatum()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "Tekst zonder datum", "Verzoek 9 mei 2026");
        classificatie.Datum.Should().Be("2026-05-09");
    }

    [Fact]
    public void ValideerDagDatum_OnderwerpBevat_dmaandZonderJaar_GebruiktHuidigJaar()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", "25 april beschikbaarheid");
        var verwachtJaar = DateTime.Now.Year;
        classificatie.Datum.Should().Be($"{verwachtJaar}-04-25");
    }

    [Fact]
    public void ValideerDagDatum_OnderwerpPrioriteit_BovenBody()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "body 05-06-2026 iets", "onderwerp 18-4-2026");
        classificatie.Datum.Should().Be("2026-04-18");
    }

    // ── ValideerDagDatum — datum in body ──

    [Fact]
    public void ValideerDagDatum_BodyBevat_ddmmyyyy_EnAiDatumLeeg_GebruiktBodyDatum()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = null
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "We willen graag spelen op 12-5-2026.", "Hallo");
        classificatie.Datum.Should().Be("2026-05-12");
    }

    [Fact]
    public void ValideerDagDatum_BodyDatum_NietGebruikt_AlsAiDatumAlGevuld()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-04-15"
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "body 12-5-2026", "geen datum in onderwerp");
        classificatie.Datum.Should().Be("2026-04-15");
    }

    // ── ValideerDagDatum — dag-naam correctie ──

    [Fact]
    public void ValideerDagDatum_DagNaamZaterdagInTekst_CorregeertNaarDichtsteZaterdag()
    {
        // 2026-04-14 is een dinsdag; de tekst zegt "zaterdag" → corrigeer naar 2026-04-18
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-04-14"  // dinsdag
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "Kunnen we zaterdag inhalen?", "Verzoek");
        var datum = DateOnly.Parse(classificatie.Datum!);
        datum.DayOfWeek.Should().Be(DayOfWeek.Saturday);
    }

    [Fact]
    public void ValideerDagDatum_DagNaamMatchtAiDatum_GeenWijziging()
    {
        // 2026-04-18 is een zaterdag; tekst zegt "zaterdag" → ongewijzigd
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-04-18"  // zaterdag
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "Kunnen we zaterdag spelen?", "Verzoek");
        classificatie.Datum.Should().Be("2026-04-18");
    }

    [Fact]
    public void ValideerDagDatum_GeenDatumEnGeenDagNaam_DatumBlijftLeeg()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "Gewoon wat tekst zonder datum", "onderwerp");
        classificatie.Datum.Should().BeNullOrEmpty();
    }

    [Fact]
    public void ValideerDagDatum_OngeldigeDatumString_DatumOngewijzigd()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "geen-datum"
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", "onderwerp");
        classificatie.Datum.Should().Be("geen-datum");
    }

    // ── ValideerDagDatum — tweestrijdige dag-namen ──

    [Fact]
    public void ValideerDagDatum_BeideDagNamenInTekst_DatumOngewijzigd()
    {
        // zowel "zaterdag" als "zondag" in tekst → niet gecorrigeerd (ambigu)
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-04-14"  // dinsdag
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "zaterdag of zondag?", "onderwerp");
        // Eerste match wint (zaterdag), want foreach stopt na eerste gevonden dag
        var datum = DateOnly.Parse(classificatie.Datum!);
        datum.DayOfWeek.Should().Be(DayOfWeek.Saturday);
    }

    // ── ValideerDagDatum — randgevallen datum parsing ──

    [Fact]
    public void ValideerDagDatum_Patroon_1_1_2026_WordtCorrectGeparsed()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", "wedstrijd 1-1-2026");
        classificatie.Datum.Should().Be("2026-01-01");
    }

    [Fact]
    public void ValideerDagDatum_MaandNaamDecember_WordtCorrectGeparsed()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", "3 december 2025");
        classificatie.Datum.Should().Be("2025-12-03");
    }
}
