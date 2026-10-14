using AwesomeAssertions;
using Planner.Shared;
using Planner.Shared.Email;
using FunctionApp.Postgres;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Planner;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Omhulseltest van de Postgres-tier (productie) voor <see cref="BerichtResponseGenerator"/> (#1758). De antwoordteksten
/// staan in Planner.Shared (<c>BerichtAntwoordGeneratorTests</c>); hier alleen wat deze tier zelf levert: de keuze
/// tussen club-snapshot en procesbrede cache (#677), de foutmeldingen van deze tier en de DTO's als poort.
/// Testklassen draaien in dit project serieel (AssemblyInfo.cs), dus de gedeelde cache is hier veilig te vullen.
/// </summary>
public class BerichtResponseGeneratorOmhulselTests : IDisposable
{
    private static readonly InkomendBericht Email = new()
    {
        MessageId = "omhulsel-test",
        Afzender = "trainer@voorbeeld.nl",
        AfzenderNaam = "Jan de Vries",
        Onderwerp = "Beschikbaarheid",
        Body = "Is er ruimte?"
    };

    private static readonly ClubAppSettingsSnapshot Voetnoot = new(null, null, null, "Testomgeving", null);

    public BerichtResponseGeneratorOmhulselTests() => PostgresAppSettings.ResetForTests();

    public void Dispose() => PostgresAppSettings.ResetForTests();

    private static void VulGlobaleCache()
    {
        PostgresAppSettings.SetForTests("plannerAfzenderNaam", "Globale planner");
        PostgresAppSettings.SetForTests("coordinatorNaam", "Globale coordinator");
        PostgresAppSettings.SetForTests("coordinatorFunctie", "Globale functie");
    }

    [Fact]
    public void MetSnapshot_GebruiktDeSnapshotOokAlsDeGlobaleCacheGevuldIs()
    {
        VulGlobaleCache();
        var snapshot = new ClubAppSettingsSnapshot("AllStars FC Testomgeving", "Frenkie", "Coördinator", null, null);

        var (_, body) = BerichtResponseGenerator.BouwBuitenScopeAntwoord(Email, snapshot);

        body.Should().EndWith("Met vriendelijke groet,\n\nAllStars FC Testomgeving\nGeautomatiseerd antwoord namens Frenkie\nCoördinator");
        body.Should().NotContain("Globale");
    }

    [Fact]
    public void ZonderSnapshot_LeestDeGlobaleCache()
    {
        VulGlobaleCache();

        var (_, body) = BerichtResponseGenerator.BouwBuitenScopeAntwoord(Email);
        var (_, multi) = BerichtResponseGenerator.BouwMultiDatumBeschikbaarheidAntwoord(
            [("2026-09-12", new CheckAvailabilityResponse { Reden = "Vol." })], new BerichtClassificatie(), Email);

        body.Should().EndWith("Met vriendelijke groet,\n\nGlobale planner\nGeautomatiseerd antwoord namens Globale coordinator\nGlobale functie");
        multi.Should().Contain("dan gaan we samen met Globale coordinator plannen");
    }

    [Fact]
    public void ZonderSnapshot_VoetnootUitDeGlobaleCacheGaatVoor()
    {
        VulGlobaleCache();
        PostgresAppSettings.SetForTests("emailVoetnoot", "Globale voetnoot");

        BerichtResponseGenerator.BouwBuitenScopeAntwoord(Email).body.Should().EndWith("\n\nGlobale voetnoot");
    }

    [Fact]
    public void MetSnapshotZonderAfzender_ValtNietTerugOpDeGlobaleCache()
    {
        VulGlobaleCache();
        var act = () => BerichtResponseGenerator.BouwBuitenScopeAntwoord(Email, new ClubAppSettingsSnapshot(null, null, null, null, null));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Vereiste instelling 'PlannerAfzenderNaam' ontbreekt in public.appsettings voor de opgegeven club");
    }

    [Fact]
    public void ZonderSnapshotEnLegeCache_GeeftDeMeldingVanDezeTier()
    {
        var act = () => BerichtResponseGenerator.BouwBuitenScopeAntwoord(Email);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Vereiste instelling 'plannerAfzenderNaam' ontbreekt in public.appsettings");
    }

    [Fact]
    public void Dtos_ZijnDePoortVanDeGedeeldeGenerator()
    {
        var conflict = new CheckAvailabilityResponse { TeamConflict = new TeamConflictInfo(), Reden = "Al een wedstrijd." };
        var vensters = new CheckAvailabilityResponse
        {
            BeschikbareVensters = [new BeschikbaarVenster { VeldNaam = "veld 5", Van = "09:00", Tot = "12:00", Opmerking = "halve veld" }]
        };
        var wedstrijd = new ZoekWedstrijdResponse { Wedstrijd = "TEST JO14-1 - Ander JO14-1", Datum = "2026-09-12", AanvangsTijd = "14:00", VeldNaam = "veld 4" };
        var opties = new HerplanCheckResponse { Alternatieven = [new SlotToewijzing { VeldNaam = "veld 2", AanvangsTijd = "16:00", EindTijd = "17:45" }] };
        var classificatie = new BerichtClassificatie { Datum = "2026-09-12" };

        BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(conflict, classificatie, Email, Voetnoot).body
            .Should().Contain("Al een wedstrijd.\n\nHierdoor kan op");
        BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(vensters, classificatie, Email, Voetnoot).body
            .Should().Contain("- veld 5: beschikbaar van 09:00 tot 12:00 (halve veld)");
        BerichtResponseGenerator.BouwHerplanAntwoord(wedstrijd, opties, classificatie, Email, Voetnoot).body
            .Should().Contain("De wedstrijd TEST JO14-1 - Ander JO14-1 staat gepland op").And.Contain("- veld 2 om 16:00 (eindigt 17:45)");
        BerichtResponseGenerator.BouwAangepasteAntwoord(new EmailTemplate("k", "Onderwerp", "Hallo {{voornaam}}"), classificatie, Email, Voetnoot)
            .Should().Be(("Onderwerp", "Hallo Jan\n\nTestomgeving"));
        EmailTemplateService.ApplyPlaceholders("{{A}}", new Dictionary<string, string> { ["a"] = "b" }).Should().Be("b");
    }
}
