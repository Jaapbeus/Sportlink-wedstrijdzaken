using AwesomeAssertions;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Processing;
using Planner.Shared.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Processing;

/// <summary>
/// #1716: het tier-omhulsel <see cref="BerichtPipeline.ValideerDagDatum"/> op de productietier. De
/// datumregels zelf staan in <c>Planner.Shared</c> (<c>BerichtDatumHeuristiek</c>) en zijn daar getest
/// (<c>Planner.Shared.Tests/Email/BerichtDatumHeuristiekTests.cs</c>). Hier alleen: het omhulsel
/// schrijft de uitkomst terug op de classificatie en laat een niet te corrigeren datum staan. Geen
/// database nodig. SQL Server-tier-tegenhanger: <c>FunctionApp.Tests/Processing/BerichtPipelineTests.cs</c>.
/// </summary>
public class BerichtPipelineDatumTests
{
    [Fact]
    public void ValideerDagDatum_OnderwerpDatum_WordtOpClassificatieGezet()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };
        BerichtPipeline.ValideerDagDatum(classificatie, "Kan jullie op die datum?", "Beschikbaarheid 18-4-2026");
        classificatie.Datum.Should().Be("2026-04-18");
    }

    [Fact]
    public void ValideerDagDatum_DagnaamCorrectie_WordtOpClassificatieGezet()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck, Datum = "2026-04-14" };
        BerichtPipeline.ValideerDagDatum(classificatie, "Kunnen we zaterdag inhalen?", "Verzoek");
        DateOnly.Parse(classificatie.Datum!).DayOfWeek.Should().Be(DayOfWeek.Saturday);
    }

    [Fact]
    public void ValideerDagDatum_NietsTeCorrigeren_LaatDatumStaan()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck, Datum = "geen-datum" };
        BerichtPipeline.ValideerDagDatum(classificatie, "tekst", "onderwerp");
        classificatie.Datum.Should().Be("geen-datum");
    }

    [Fact]
    public void ValideerDagDatum_Herplan_OnderwerpdatumVervangtHuidigeDatumNiet()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.HerplanVerzoek,
            Datum = "2026-04-18",
            GewensteDatum = "2026-04-25"
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "Wij willen verzetten naar 25-4-2026.", "Administratie 12-5-2026");
        classificatie.Datum.Should().Be("2026-04-18");
        classificatie.GewensteDatum.Should().Be("2026-04-25");
    }

    [Fact]
    public void ValideerDagDatum_EnkeleGelijkeLijstdatumVolgtCorrectie()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-04-18",
            Datums = new List<string> { "2026-04-18" }
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "Kunnen we dinsdag spelen?", "Verzoek");
        classificatie.Datum.Should().Be("2026-04-21");
        classificatie.GetAlleDatums().Should().Equal("2026-04-21");
    }

    [Fact]
    public void ValideerDagDatum_AndereLijstdatumBlijftStaan()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-04-18",
            Datums = new List<string> { "2026-04-25" }
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "Kunnen we dinsdag spelen?", "Verzoek");
        classificatie.GetAlleDatums().Should().Equal("2026-04-25");
    }

    [Fact]
    public void ValideerDagDatum_MeerdereDatumsBlijvenStaan()
    {
        var classificatie = new BerichtClassificatie
        {
            Type = VerzoekType.BeschikbaarheidCheck,
            Datum = "2026-04-18",
            Datums = new List<string> { "2026-04-18", "2026-04-25" }
        };
        BerichtPipeline.ValideerDagDatum(classificatie, "Kunnen we dinsdag spelen?", "Verzoek");
        classificatie.GetAlleDatums().Should().Equal("2026-04-18", "2026-04-25");
    }
}
