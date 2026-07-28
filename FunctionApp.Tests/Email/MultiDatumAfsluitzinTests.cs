using FluentAssertions;
using SportlinkFunction.Email;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Tests voor issue #670: de afsluitzin van een multi-datum beschikbaarheidsantwoord noemt de
/// coördinator bij naam.
///
/// <para>
/// De naam wordt via dezelfde weg gelezen als de handtekening: uit de meegegeven club-snapshot
/// wanneer die er is, en anders uit de proces-globale cache. Dat is de regel uit #677 — een dry-run
/// voor de democlub mag niet terugvallen op de gegevens van de productieclub. Zonder live database is
/// die globale cache in dit testproces nooit geladen, wat hem bruikbaar maakt als kanarie: valt de
/// code ooit alsnog terug, dan gooit dat een <see cref="InvalidOperationException"/> in plaats van
/// stilzwijgend de verkeerde club te tonen.
/// </para>
/// </summary>
public class MultiDatumAfsluitzinTests
{
    private static InkomendBericht MaakEmail() => new()
    {
        MessageId = "multi-datum-test",
        ConversationId = "",
        Afzender = "trainer@voorbeeld.nl",
        AfzenderNaam = "Jan de Vries",
        Onderwerp = "Beschikbaarheid",
        OntvangstDatum = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
        Body = "Kan het 12 of 19 september?"
    };

    private static BerichtClassificatie MaakClassificatie() => new()
    {
        Type = VerzoekType.BeschikbaarheidCheck,
        Datums = new List<string> { "2026-09-12", "2026-09-19" },
        LeeftijdsCategorie = "JO13",
        Samenvatting = "Beschikbaarheid voor twee zaterdagen"
    };

    private static List<(string datum, CheckAvailabilityResponse response)> MaakResultaten() => new()
    {
        ("2026-09-12", new CheckAvailabilityResponse { Beschikbaar = true }),
        ("2026-09-19", new CheckAvailabilityResponse { Beschikbaar = false, Reden = "Geen ruimte" })
    };

    [Fact]
    public void MetCoordinatorInSnapshot_NoemtDeCoordinatorInDeAfsluitzin()
    {
        var snapshot = new ClubAppSettingsSnapshot(
            PlannerAfzenderNaam: "AllStars FC Testomgeving",
            CoordinatorNaam: "Frenkie",
            CoordinatorFunctie: "Coördinator",
            EmailVoetnoot: null,
            HerplanDeadlineDagen: 3);

        var (_, body) = BerichtResponseGenerator.BouwMultiDatumBeschikbaarheidAntwoord(
            MaakResultaten(), MaakClassificatie(), MaakEmail(), snapshot);

        body.Should().Contain("samen met Frenkie plannen",
            "de afsluitzin van een multi-datum antwoord noemt de coördinator bij naam (#670)");
    }

    /// <summary>
    /// Ontbreekt de naam, dan moet de zin nog steeds een volledige, clubneutrale zin zijn — geen
    /// halve zin en geen fallbacknaam in de code (architectuurregel "geen club-specifieke strings").
    /// </summary>
    [Fact]
    public void ZonderCoordinatorInSnapshot_BlijftDeZinCompleetEnClubneutraal()
    {
        var snapshot = new ClubAppSettingsSnapshot(
            PlannerAfzenderNaam: "AllStars FC Testomgeving",
            CoordinatorNaam: null,
            CoordinatorFunctie: null,
            EmailVoetnoot: null,
            HerplanDeadlineDagen: 3);

        var (_, body) = BerichtResponseGenerator.BouwMultiDatumBeschikbaarheidAntwoord(
            MaakResultaten(), MaakClassificatie(), MaakEmail(), snapshot);

        body.Should().Contain("dan gaan we samen plannen en definitief opnemen in de planning.");
        body.Should().NotContain("samen met  plannen", "een ontbrekende naam mag geen dubbele spatie opleveren");
    }

    /// <summary>
    /// De coördinator uit de snapshot mag niet worden overschreven door de globale cache. Die cache is
    /// in dit testproces leeg, dus een terugval zou hier direct opvallen.
    /// </summary>
    [Fact]
    public void MetSnapshot_ValtNietTerugOpDeGlobaleCache()
    {
        var snapshot = new ClubAppSettingsSnapshot(
            PlannerAfzenderNaam: "AllStars FC Testomgeving",
            CoordinatorNaam: "Frenkie",
            CoordinatorFunctie: "Coördinator",
            EmailVoetnoot: null,
            HerplanDeadlineDagen: 3);

        var act = () => BerichtResponseGenerator.BouwMultiDatumBeschikbaarheidAntwoord(
            MaakResultaten(), MaakClassificatie(), MaakEmail(), snapshot);

        act.Should().NotThrow<InvalidOperationException>(
            "met een expliciete club-snapshot mag de globale AppSettings-cache niet worden geraadpleegd (#677)");
    }
}
