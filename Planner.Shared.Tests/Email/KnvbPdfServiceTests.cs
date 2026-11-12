using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>
/// #1813: de KNVB-kalender-PDF-lezer staat eenmalig in Planner.Shared voor beide databasetiers.
/// Elke test gebruikt een eigen fictief seizoen, omdat de procesbrede bytes-cache per seizoen/bestand
/// sleutelt en anders tussen tests zou lekken.
/// </summary>
public sealed class KnvbPdfServiceTests : IDisposable
{
    private readonly string _basisMap = Path.Combine(Path.GetTempPath(), "knvbpdf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_basisMap))
            Directory.Delete(_basisMap, recursive: true);
    }

    private void SchrijfPdf(string seizoenSlug, string bestandsnaam, byte[] inhoud)
    {
        var map = Path.Combine(_basisMap, "Content", "KnvbKalenders", seizoenSlug);
        Directory.CreateDirectory(map);
        File.WriteAllBytes(Path.Combine(map, bestandsnaam), inhoud);
    }

    [Fact]
    public async Task LeestPdfMetRegioSlugEnSeizoenMetStreepje()
    {
        SchrijfPdf("2091-2092", "speeldagenkalender-veld-landelijk-jeugd-2091-2092.pdf", [1, 2, 3]);

        var bijlage = await KnvbPdfService.GetKalenderPdfAsync("landelijkjeugd", "2091/2092", NullLogger.Instance, _basisMap);

        bijlage.Should().NotBeNull();
        bijlage!.Bestandsnaam.Should().Be("speeldagenkalender-veld-landelijk-jeugd-2091-2092.pdf");
        bijlage.Inhoud.Should().Equal(1, 2, 3);
        bijlage.ContentType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task GebruiktCacheNaEersteLezing()
    {
        SchrijfPdf("2093-2094", "speeldagenkalender-veld-west-2093-2094.pdf", [7]);
        await KnvbPdfService.GetKalenderPdfAsync("West", "2093/2094", NullLogger.Instance, _basisMap);
        Directory.Delete(_basisMap, recursive: true);

        var bijlage = await KnvbPdfService.GetKalenderPdfAsync("West", "2093/2094", NullLogger.Instance, _basisMap);

        bijlage!.Inhoud.Should().Equal(7);
    }

    [Theory]
    [InlineData("", "2095/2096")]
    [InlineData("West", " ")]
    [InlineData("Midden", "2095/2096")]
    public async Task OntbrekendeOfOnbekendeInvoerGeeftNull(string regio, string seizoen)
    {
        SchrijfPdf("2095-2096", "speeldagenkalender-veld-west-2095-2096.pdf", [1]);
        SchrijfPdf("2095-2096", "speeldagenkalender-veld-midden-2095-2096.pdf", [1]);

        (await KnvbPdfService.GetKalenderPdfAsync(regio, seizoen, NullLogger.Instance, _basisMap)).Should().BeNull();
    }

    [Fact]
    public async Task OntbrekendBestandGeeftNullZonderException()
    {
        (await KnvbPdfService.GetKalenderPdfAsync("Oost", "2097/2098", NullLogger.Instance, _basisMap)).Should().BeNull();
    }

    [Fact]
    public async Task LeesfoutGeeftNullZonderException()
    {
        SchrijfPdf("2101-2102", "speeldagenkalender-veld-noord-2101-2102.pdf", [1]);
        var pad = Path.Combine(_basisMap, "Content", "KnvbKalenders", "2101-2102", "speeldagenkalender-veld-noord-2101-2102.pdf");

        // Exclusief geopend: lezen faalt met een IOException (Windows-share-lock, op Unix door .NET
        // met een advisory lock nagebootst), zodat het catch-pad wordt geraakt.
        EmailBijlage? bijlage;
        using (new FileStream(pad, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bijlage = await KnvbPdfService.GetKalenderPdfAsync("Noord", "2101/2102", NullLogger.Instance, _basisMap);
        }

        bijlage.Should().BeNull();
    }

    [Fact]
    public async Task OngeldigeBasismapGeeftNullZonderException()
    {
        var bijlage = await KnvbPdfService.GetKalenderPdfAsync("Zuid", "2099/2100", NullLogger.Instance, "\0");

        bijlage.Should().BeNull();
    }
}
