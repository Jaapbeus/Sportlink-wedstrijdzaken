using FluentAssertions;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Regressietests voor issue #677: de Email-tester (dry-run pad) moet de auto-reply handtekening
/// (afzendernaam, coördinator, voetnoot) opbouwen uit de instellingen van de in de GUI
/// geselecteerde club (bijv. AllStars FC), niet uit de proces-globale <c>SystemUtilities.AppSettings</c>
/// cache — die bevat altijd de instellingen van de echte productieclub.
///
/// Zonder een live SQL-database is die globale cache in dit testproces per definitie nooit geladen
/// (leeg). Dat maakt hem bruikbaar als "kanarie": als <c>GetHandtekening</c> bij een expliciete
/// <see cref="ClubAppSettingsSnapshot"/>-override ooit weer op de globale cache zou terugvallen,
/// gooit deze test een <see cref="InvalidOperationException"/> in plaats van de snapshot-waarde
/// te tonen — dat is precies de regressie die #677 beschreef (de echte club lekt in een
/// AllStars-dry-run).
/// </summary>
public class BerichtResponseGeneratorClubSettingsTests
{
    private static InkomendBericht MaakEmail() => new()
    {
        MessageId = "dry-run-test",
        ConversationId = "",
        Afzender = "trainer@voorbeeld.nl",
        AfzenderNaam = "Jan de Vries",
        Onderwerp = "Beschikbaarheid",
        OntvangstDatum = DateTime.UtcNow,
        Body = "Kunnen we zaterdag spelen?"
    };

    [Fact]
    public void BouwBuitenScopeAntwoord_MetClubSettings_GebruiktSnapshotAfzenderEnCoordinator()
    {
        var snapshot = new ClubAppSettingsSnapshot(
            PlannerAfzenderNaam: "AllStars FC Testomgeving",
            CoordinatorNaam: "Frenkie",
            CoordinatorFunctie: "Coördinator",
            EmailVoetnoot: null,
            HerplanDeadlineDagen: 3);

        var (_, body) = BerichtResponseGenerator.BouwBuitenScopeAntwoord(MaakEmail(), snapshot);

        body.Should().Contain("AllStars FC Testomgeving");
        body.Should().Contain("Frenkie");
        body.Should().Contain("Coördinator");
    }

    [Fact]
    public void BouwBuitenScopeAntwoord_MetEmailVoetnootInSnapshot_GebruiktVoetnootUitSnapshot()
    {
        var snapshot = new ClubAppSettingsSnapshot(
            PlannerAfzenderNaam: null,
            CoordinatorNaam: null,
            CoordinatorFunctie: null,
            EmailVoetnoot: "Groeten van AllStars FC (testomgeving)",
            HerplanDeadlineDagen: null);

        var (_, body) = BerichtResponseGenerator.BouwBuitenScopeAntwoord(MaakEmail(), snapshot);

        body.Should().Contain("Groeten van AllStars FC (testomgeving)");
    }

    [Fact]
    public void BouwBuitenScopeAntwoord_ClubSettingsZonderAfzenderNaam_ValtNietTerugOpGlobaleCache()
    {
        // Ontbreekt PlannerAfzenderNaam voor déze club, dan mag GetHandtekening NIET stilzwijgend
        // de instelling van een andere club tonen (de globale cache) — dat zou opnieuw de bug van
        // #677 zijn. In plaats daarvan een expliciete fout, net als het bestaande gedrag zonder
        // override wanneer de verplichte instelling ontbreekt.
        var snapshot = new ClubAppSettingsSnapshot(
            PlannerAfzenderNaam: null,
            CoordinatorNaam: null,
            CoordinatorFunctie: null,
            EmailVoetnoot: null,
            HerplanDeadlineDagen: null);

        Action act = () => BerichtResponseGenerator.BouwBuitenScopeAntwoord(MaakEmail(), snapshot);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void BouwBuitenScopeAntwoord_ZonderClubSettings_ValtTerugOpGlobaleCache_DieLeegIsInTests()
    {
        // Baseline: bevestigt dat het gedrag voor de echte e-mailpipeline (clubSettings = null,
        // zie EmailProcessorFunction) ongewijzigd is — nog steeds de globale cache, die in dit
        // testproces zonder live DB nooit geladen is en dus op dezelfde manier faalt als vóór #677.
        Action act = () => BerichtResponseGenerator.BouwBuitenScopeAntwoord(MaakEmail());

        act.Should().Throw<InvalidOperationException>();
    }
}
