using AwesomeAssertions;
using Database.Postgres;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// #1225 — regressietest op de foutuitvoer van <c>Database.Postgres.Cli</c>. Die CLI draait in de
/// job <c>db-migrate-postgres</c> van <c>deploy.yml</c>, en de Actions-logs van deze repository
/// zijn publiek. Vóór #1225 schreef de CLI <c>ex.Message</c> naar stderr; een Npgsql-verbindingsfout
/// luidt "Failed to connect to &lt;host&gt;:&lt;poort&gt;" en een authenticatiefout kan de
/// gebruikersnaam noemen — beide deelstrings van <c>POSTGRES_CONNECTION_STRING</c>, die GitHub
/// niet maskeert (alleen de exacte, volledige secretwaarde).
/// <para>
/// Deze tests hebben <b>geen database nodig</b> en draaien dus altijd mee: de eerste twee toetsen
/// de opmaak rechtstreeks, de derde lokt een echte Npgsql-fout uit tegen een synthetische,
/// onbereikbare host. Alle gebruikte waarden zijn verzonnen — geen echte host, gebruiker of club.
/// </para>
/// </summary>
public class MigratieFoutRapportageTests
{
    // Synthetische, bewust onbereikbare verbindingsgegevens. Geen wachtwoord: Npgsql heeft er geen
    // nodig om te falen, en een 'wachtwoord'-sleutel in een testbestand is een patroon dat we hier
    // sowieso niet willen hebben staan.
    private const string OnbereikbareHost = "nonexistent-host-voor-test";
    private const string OnbereikbarePoort = "59999";
    private const string TestGebruiker = "migratietestgebruiker";

    private static string SynthetischeConnectiestring =>
        $"Host={OnbereikbareHost};Port={OnbereikbarePoort};Username={TestGebruiker};" +
        "Database=migratietestdb;Timeout=2;Command Timeout=2";

    [Fact]
    public void Beschrijf_NoemtHandelingStapEnExceptietype()
    {
        var fout = new InvalidOperationException("Failed to connect to " + OnbereikbareHost);

        var regel = MigratieFoutRapportage.Beschrijf("Migratie", fout, "021_enable_row_level_security.sql");

        regel.Should().Contain("Migratie mislukt");
        regel.Should().Contain("021_enable_row_level_security.sql",
            "zonder de migratiestap is de job onbruikbaar om een fout mee te zoeken");
        regel.Should().Contain(nameof(InvalidOperationException),
            "het exceptietype is de andere helft van de diagnose en bevat nooit gebruikersdata");
    }

    [Fact]
    public void Beschrijf_NeemtDeExceptietekstNooitOver()
    {
        // Exact de vorm die Npgsql produceert bij een onbereikbare database.
        var fout = new InvalidOperationException(
            $"Failed to connect to {OnbereikbareHost}:{OnbereikbarePoort} als gebruiker {TestGebruiker}");

        var regel = MigratieFoutRapportage.Beschrijf("Migratie", fout);

        regel.Should().NotContain(OnbereikbareHost);
        regel.Should().NotContain(OnbereikbarePoort);
        regel.Should().NotContain(TestGebruiker);
        regel.Should().NotContain("Failed to connect");
    }

    [Fact]
    public async Task Beschrijf_BijEchteVerbindingsfout_LektHostPoortNochGebruiker()
    {
        var migratiemap = Directory.CreateTempSubdirectory("migratiefout-rapportage-test");
        try
        {
            Exception? gevangen = null;
            string? huidigeMigratie = null;
            try
            {
                await MigrationRunner.RunAsync(
                    SynthetischeConnectiestring,
                    migratiemap.FullName,
                    onMigratieStart: naam => huidigeMigratie = naam);
            }
            catch (Exception ex)
            {
                gevangen = ex;
            }

            gevangen.Should().NotBeNull("een onbereikbare host moet de runner laten falen");
            huidigeMigratie.Should().BeNull("de verbinding faalt vóór het eerste migratiebestand");

            var regel = MigratieFoutRapportage.Beschrijf("Migratie", gevangen!, huidigeMigratie);

            regel.Should().Contain("Migratie mislukt");
            regel.Should().Contain(gevangen!.GetType().Name);

            regel.Should().NotContain(OnbereikbareHost, "de host is een deelstring van de connectiestring");
            regel.Should().NotContain(OnbereikbarePoort, "de poort is een deelstring van de connectiestring");
            regel.Should().NotContain(TestGebruiker, "de gebruikersnaam draagt bij de hostingprovider de projectidentificatie");
        }
        finally
        {
            migratiemap.Delete(recursive: true);
        }
    }
}
