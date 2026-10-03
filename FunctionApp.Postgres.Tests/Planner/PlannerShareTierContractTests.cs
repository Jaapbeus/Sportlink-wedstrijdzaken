using System.Text;
using AwesomeAssertions;
using Planner.Shared.Deel;
using FunctionApp.Postgres.Planner;
using Xunit;

namespace FunctionApp.Postgres.Tests.Planner;

/// <summary>
/// #1363, Postgres-tier: de echte wire-DTO's van deze tier gaan zonder eigen mapping de gedeelde
/// deel-/PDF-laag in. Tegenhanger: <c>FunctionApp.Tests/Planner/PlannerShareTierContractTests.cs</c>.
/// Faalt de build hier, dan is een eigenschap van <see cref="VeldbezettingItem"/> of
/// <see cref="AutoPlanWedstrijdItem"/> hernoemd en loopt deze tier uit de pas met de gedeelde laag.
/// </summary>
public class PlannerShareTierContractTests
{
    private static readonly DateOnly Zaterdag = new(2026, 10, 3);

    [Fact]
    public void Veldbezetting_VanDezeTier_LevertEenPdf()
    {
        // Op deze tier is het veldbezettingsitem een positioneel record.
        var items = new List<VeldbezettingItem>
        {
            new(WedstrijdCode: 1, Wedstrijd: "AllStars JO10-1 - Gasten JO10-2", TeamNaam: "JO10-1",
                Uitteam: "Gasten JO10-2", AanvangsTijd: "09:30", Veld: "veld 3 A", Competitiesoort: "competitie",
                LeeftijdsCategorie: "JO10", DuurMinuten: 60, Veldafmeting: 0.5m),
        };

        var model = PlannerShareModelBuilder.VanVeldbezetting(items, Zaterdag, "ALLSTARS");
        var pdf = PlannerPdfGenerator.Genereer(model, true);

        model.Wedstrijden.Single().Should().Be(
            new PlannerShareWedstrijd("09:30", "JO10-1", "Gasten JO10-2", "veld 3 A", "competitie", null));
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public void AutoPlan_VanDezeTier_KiestDeOptimaleKant()
    {
        var items = new List<AutoPlanWedstrijdItem>
        {
            new() { TeamNaam = "JO13-2", Wedstrijd = "AllStars JO13-2 - Gasten JO13-1", Competitiesoort = "beker",
                    HuidigeTijd = "10:00", HuidigeVeld = "veld 1", OptimaalTijd = "09:15", OptimaalVeld = "veld 2 B" },
        };

        var model = PlannerShareModelBuilder.VanPlan(items, Zaterdag, "ALLSTARS", PlanWeergave.Optimaal);

        model.Wedstrijden.Single().Should().Be(
            new PlannerShareWedstrijd("09:15", "JO13-2", "Gasten JO13-1", "veld 2 B", "beker", null));
    }

    [Fact]
    public void Pdf_ZonderIngeschakeldeClubinstelling_WordtGeweigerd()
    {
        // #1459: PDF-export staat per club standaard uit; de generator is de laatste vangrail.
        var model = PlannerShareModelBuilder.VanVeldbezetting(new List<VeldbezettingItem>(), Zaterdag, "ALLSTARS");

        var act = () => PlannerPdfGenerator.Genereer(model, false);

        act.Should().Throw<InvalidOperationException>();
    }
}
