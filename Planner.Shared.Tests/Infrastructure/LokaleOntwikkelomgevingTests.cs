using AwesomeAssertions;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

/// <summary>
/// De beslisregel van de lokale ontwikkelbypass (#1700), los van de procesomgeving: elke test geeft
/// zijn eigen variabelen mee. Faalt veilig: zonder schakelaar geen bypass, en de schakelaar telt niet
/// als de app in Azure draait. De tiers gebruiken deze regel via een dunne adapter
/// (<c>EasyAuthHelper</c>, <c>EgressGuard</c>); hun eigen tests bewijzen de bedrading per endpoint.
/// </summary>
public class LokaleOntwikkelomgevingTests
{
    private const string Azure = "func-proef-01";

    private static Func<string, string?> Omgeving(string? schakelaar = null, string? azure = null, string? optIn = null)
    {
        var waarden = new Dictionary<string, string?>
        {
            [LokaleOntwikkelomgeving.Schakelaar] = schakelaar,
            [LokaleOntwikkelomgeving.AzureHostingVariabele] = azure,
            [LokaleOntwikkelomgeving.ExterneIntegratiesOptIn] = optIn,
        };
        return naam => waarden.TryGetValue(naam, out var w) ? w : null;
    }

    // ── Stand van de bypass ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ZonderSchakelaar_BuitenAzure_IsUit()
        => LokaleOntwikkelomgeving.Bepaal(Omgeving()).Should().Be(LokaleBypassStand.Uit,
            "de afwezigheid van een platformvariabele is geen toestemming meer (#1700)");

    [Fact]
    public void ZonderSchakelaar_InAzure_IsUit()
        => LokaleOntwikkelomgeving.Bepaal(Omgeving(azure: Azure)).Should().Be(LokaleBypassStand.Uit);

    [Fact]
    public void MetSchakelaar_BuitenAzure_IsActief()
        => LokaleOntwikkelomgeving.Bepaal(Omgeving(schakelaar: "true")).Should().Be(LokaleBypassStand.Actief);

    [Fact]
    public void MetSchakelaar_InAzure_WordtGeweigerd()
        => LokaleOntwikkelomgeving.Bepaal(Omgeving(schakelaar: "true", azure: Azure))
            .Should().Be(LokaleBypassStand.GeweigerdInAzure, "een gehoste app krijgt nooit de lokale bypass");

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData(" true ")]
    public void Schakelaar_AlleenTrueTelt_HoofdletterOngevoelig(string waarde)
        => LokaleOntwikkelomgeving.BypassActief(Omgeving(schakelaar: waarde)).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("ja")]
    [InlineData("on")]
    [InlineData("truee")]
    public void Schakelaar_AndereWaarde_GeeftGeenBypass(string waarde)
        => LokaleOntwikkelomgeving.BypassActief(Omgeving(schakelaar: waarde)).Should().BeFalse(
            "alleen de letterlijke waarde true opent de bypass; elke andere waarde faalt dicht");

    [Fact]
    public void BypassActief_InAzure_IsFalse()
        => LokaleOntwikkelomgeving.BypassActief(Omgeving(schakelaar: "true", azure: Azure)).Should().BeFalse();

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(Azure, true)]
    public void DraaitInAzure_VolgtDePlatformvariabele(string? waarde, bool verwacht)
        => LokaleOntwikkelomgeving.DraaitInAzure(Omgeving(azure: waarde)).Should().Be(verwacht);

    // ── Uitgaande integraties (EgressGuard) ────────────────────────────────────────────────────

    [Theory]
    // schakelaar, azure, opt-in, verwacht
    [InlineData(null, null, null, false)]       // CI, losse func start: dicht zoals sinds #857
    [InlineData(null, Azure, null, true)]       // productie
    [InlineData("true", null, null, false)]     // lokale ontwikkeling: dicht
    [InlineData("true", Azure, null, false)]    // geweigerde schakelaar in Azure: dicht, bij twijfel dicht
    [InlineData(null, null, "true", true)]      // bewuste handmatige test
    [InlineData("true", null, "true", true)]    // lokaal met opt-in (-SportlinkLive)
    [InlineData("true", Azure, "true", true)]   // opt-in blijft de enige expliciete opening
    [InlineData(null, null, "false", false)]
    [InlineData(null, Azure, "false", true)]    // opt-in false sluit productie niet af (ongewijzigd)
    public void ExterneIntegraties_Matrix(string? schakelaar, string? azure, string? optIn, bool verwacht)
        => LokaleOntwikkelomgeving.ExterneIntegratiesToegestaan(Omgeving(schakelaar, azure, optIn)).Should().Be(verwacht);

    [Theory]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void ExterneIntegraties_OptInIsHoofdletterOngevoelig(string waarde)
        => LokaleOntwikkelomgeving.ExterneIntegratiesToegestaan(Omgeving(optIn: waarde)).Should().BeTrue();

    // ── Opstartmelding: geaggregeerd, zonder waarden ───────────────────────────────────────────

    [Fact]
    public void MeldOpstart_GeweigerdInAzure_WaarschuwtEenmaalZonderWaarden()
    {
        var uit = new StringWriter();

        LokaleOntwikkelomgeving.MeldOpstart(uit, Omgeving(schakelaar: "true", azure: Azure));

        var regels = uit.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        regels.Should().ContainSingle();
        regels[0].Should().StartWith("WAARSCHUWING").And.Contain(LokaleOntwikkelomgeving.Schakelaar).And.Contain("geweigerd");
        regels[0].Should().NotContain(Azure, "de melding noemt nooit de waarde van een omgevingsvariabele");
    }

    [Fact]
    public void MeldOpstart_ActiefLokaal_MeldtEenmaal()
    {
        var uit = new StringWriter();

        LokaleOntwikkelomgeving.MeldOpstart(uit, Omgeving(schakelaar: "true"));

        uit.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Should().ContainSingle().Which.Should().Contain("rolcontrole wordt overgeslagen");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Azure)]
    public void MeldOpstart_ZonderSchakelaar_ZwijgtVolledig(string? azure)
    {
        var uit = new StringWriter();

        LokaleOntwikkelomgeving.MeldOpstart(uit, Omgeving(azure: azure));

        uit.ToString().Should().BeEmpty("productiegedrag is de standaard en vraagt geen melding");
        LokaleOntwikkelomgeving.OpstartMelding(Omgeving(azure: azure)).Should().BeNull();
    }
}
