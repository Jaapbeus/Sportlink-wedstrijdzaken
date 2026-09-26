using System.Text.Json;
using AwesomeAssertions;
using SportlinkTokenCapture;
using Xunit;

namespace Tools.SportlinkTokenCapture.Tests;

/// <summary>
/// <see cref="Program.WriteRefreshTokenToSettings"/> schrijft een opgevangen refresh-token in
/// <c>local.settings.json</c> (epic #986).
/// </summary>
/// <remarks>
/// <para>
/// Waarom juist dit stukje tests krijgt: het herschrijft een bestaand JSON-bestand dat óók de
/// connectiestring en andere lokale instellingen bevat. Gaat daar iets mis, dan is de ontwikkelaar
/// zijn hele lokale configuratie kwijt — en dat valt pas op bij de volgende <c>func start</c>.
/// De rest van dit programma start een browser en praat met Sportlink; dat is geen unittest.
/// </para>
/// <para>
/// Alle tokenwaarden hieronder zijn verzonnen. Er staat nooit een echt token in een test.
/// </para>
/// </remarks>
public class WriteRefreshTokenToSettingsTests : IDisposable
{
    private readonly string _pad = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_pad)) File.Delete(_pad);
        GC.SuppressFinalize(this);
    }

    private void Schrijf(string json) => File.WriteAllText(_pad, json);

    private JsonDocument Lees() => JsonDocument.Parse(File.ReadAllText(_pad));

    [Fact]
    public void Voegt_het_token_toe_binnen_Values()
    {
        Schrijf("""{"IsEncrypted": false, "Values": {"AzureWebJobsStorage": ""}}""");

        Program.WriteRefreshTokenToSettings(_pad, "SportlinkClubRefreshToken__Wedstrijdzaken", "verzonnen-token-1");

        using var doc = Lees();
        doc.RootElement.GetProperty("Values")
           .GetProperty("SportlinkClubRefreshToken__Wedstrijdzaken").GetString()
           .Should().Be("verzonnen-token-1");
    }

    [Fact]
    public void Laat_andere_instellingen_ongemoeid()
    {
        // Dit is de eigenlijke zorg: de connectiestring van de ontwikkelaar staat in hetzelfde bestand.
        Schrijf("""
            {"IsEncrypted": false,
             "Values": {"AzureWebJobsStorage": "UseDevelopmentStorage=true", "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated"},
             "Host": {"CORS": "*"}}
            """);

        Program.WriteRefreshTokenToSettings(_pad, "SportlinkClubRefreshToken__Wedstrijdzaken", "verzonnen-token-2");

        using var doc = Lees();
        var values = doc.RootElement.GetProperty("Values");
        values.GetProperty("AzureWebJobsStorage").GetString().Should().Be("UseDevelopmentStorage=true");
        values.GetProperty("FUNCTIONS_WORKER_RUNTIME").GetString().Should().Be("dotnet-isolated");
        doc.RootElement.GetProperty("Host").GetProperty("CORS").GetString().Should().Be("*");
        doc.RootElement.GetProperty("IsEncrypted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Vervangt_een_bestaand_token_in_plaats_van_het_te_verdubbelen()
    {
        Schrijf("""{"Values": {"SportlinkClubRefreshToken__Wedstrijdzaken": "oud-verzonnen-token"}}""");

        Program.WriteRefreshTokenToSettings(_pad, "SportlinkClubRefreshToken__Wedstrijdzaken", "nieuw-verzonnen-token");

        var ruw = File.ReadAllText(_pad);
        ruw.Should().NotContain("oud-verzonnen-token");
        // Twee keer dezelfde sleutel zou geldige JSON zijn maar onvoorspelbaar uitgelezen worden.
        CountOccurrences(ruw, "SportlinkClubRefreshToken__Wedstrijdzaken").Should().Be(1);
    }

    [Fact]
    public void Tokens_van_verschillende_rollen_staan_naast_elkaar()
    {
        // Elke rol heeft een eigen Sportlink-account en dus een eigen token (#988). Het ene mag
        // het andere niet overschrijven.
        Schrijf("""{"Values": {"SportlinkClubRefreshToken__Secretariaat": "verzonnen-secretariaat"}}""");

        Program.WriteRefreshTokenToSettings(_pad, "SportlinkClubRefreshToken__Wedstrijdzaken", "verzonnen-wedstrijdzaken");

        using var doc = Lees();
        var values = doc.RootElement.GetProperty("Values");
        values.GetProperty("SportlinkClubRefreshToken__Secretariaat").GetString().Should().Be("verzonnen-secretariaat");
        values.GetProperty("SportlinkClubRefreshToken__Wedstrijdzaken").GetString().Should().Be("verzonnen-wedstrijdzaken");
    }

    [Fact]
    public void Een_bestand_zonder_Values_object_faalt_luid_in_plaats_van_stil()
    {
        // Zonder deze controle liep de methode netjes door, schreef het bestand terug zónder het
        // token, en meldde de aanroeper succes. De ontwikkelaar zoekt dan naar een token dat er
        // nooit in is gezet (#1302).
        Schrijf("""{"IsEncrypted": false}""");

        var fout = Assert.Throws<InvalidOperationException>(
            () => Program.WriteRefreshTokenToSettings(_pad, "SportlinkClubRefreshToken__Wedstrijdzaken", "verzonnen-token-3"));

        fout.Message.Should().Contain("Values");
    }

    [Fact]
    public void De_uitvoer_blijft_leesbare_JSON()
    {
        Schrijf("""{"Values": {"A": "1"}}""");

        Program.WriteRefreshTokenToSettings(_pad, "K", "v");

        File.ReadAllText(_pad).Should().Contain("\n", "het bestand wordt met Indented = true geschreven");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var aantal = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            aantal++;
        return aantal;
    }
}

public class SettingsKeyForTests
{
    [Theory]
    [InlineData("Wedstrijdzaken", "SportlinkClubRefreshToken__Wedstrijdzaken")]
    [InlineData("Secretariaat", "SportlinkClubRefreshToken__Secretariaat")]
    public void Bouwt_de_instellingennaam_per_rol(string rol, string verwacht)
        => Program.SettingsKeyFor(rol).Should().Be(verwacht);

    [Fact]
    public void Gebruikt_dubbele_underscores_zoals_de_configuratiebinding_verwacht()
        // '__' is de scheiding die .NET-configuratie naar een genest pad vertaalt; één underscore
        // zou een andere instelling opleveren dan de FunctionApp uitleest.
        => Program.SettingsKeyFor("Wedstrijdzaken").Should().Contain("__");
}
