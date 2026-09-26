using AwesomeAssertions;
using Planner.Shared.Monitoring;
using Xunit;

namespace Planner.Shared.Tests.Monitoring;

/// <summary>
/// Tests voor de gedeelde beslislogica van de database-uitvalmonitor (#1268).
///
/// <para>
/// Deze logica stond tot #1268 alleen in <c>FunctionApp/Monitoring/DatabaseUitvalMonitorFunction.cs</c>
/// en werd alleen indirect getest, via de SQL Server-tier. Zodra dezelfde regels op twee tiers moeten
/// gelden, is dat te weinig: #1248/#1252 lieten zien wat er gebeurt als twee kopieën van dezelfde
/// regel allebei zonder eigen test bestaan. Deze suite test de regels los van beide tiers.
/// </para>
/// </summary>
public class DatabaseUitvalCoreTests
{
    private static readonly DateTime Nu = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    private static DatabaseStatusInfo Status(
        string ruw, DatabaseBeschikbaarheid beschikbaarheid, DateTime? sinds = null)
        => new(ruw, beschikbaarheid, sinds);

    // ---------------------------------------------------------------------------------------
    // Statusvertaling — Azure SQL
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Paused")]
    [InlineData("paused")]
    [InlineData("PAUSED")]
    public void AzureSql_PausedIsUitval_OngeachtHoofdletters(string ruweStatus)
        => DatabaseUitvalCore.BepaalAzureSqlBeschikbaarheid(ruweStatus)
            .Should().Be(DatabaseBeschikbaarheid.Uitgevallen);

    [Theory]
    [InlineData("Online")]
    [InlineData("Pausing")]
    [InlineData("Resuming")]
    [InlineData("Unknown")]
    public void AzureSql_AllesBehalvePausedIsBeschikbaar(string ruweStatus)
        => DatabaseUitvalCore.BepaalAzureSqlBeschikbaarheid(ruweStatus)
            .Should().Be(DatabaseBeschikbaarheid.Beschikbaar,
                "dit was het gedrag vóór #1268 en de port mag het niet stilzwijgend aanscherpen");

    // ---------------------------------------------------------------------------------------
    // Statusvertaling — beheerde Postgres. De waardenlijst komt uit de OpenAPI-specificatie van de
    // Management API, niet uit een voorbeeldrespons.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void BeheerdePostgres_AlleenActiveHealthyIsBeschikbaar()
    {
        DatabaseUitvalCore.BepaalBeheerdePostgresBeschikbaarheid("ACTIVE_HEALTHY")
            .Should().Be(DatabaseBeschikbaarheid.Beschikbaar);

        DatabaseUitvalCore.BepaalBeheerdePostgresBeschikbaarheid("ACTIVE_UNHEALTHY")
            .Should().Be(DatabaseBeschikbaarheid.Onbepaald,
                "het project draait, maar of de database bruikbaar is staat niet vast");
    }

    [Theory]
    [InlineData("INACTIVE")]
    [InlineData("PAUSING")]
    [InlineData("PAUSE_FAILED")]
    [InlineData("GOING_DOWN")]
    [InlineData("INIT_FAILED")]
    [InlineData("RESTORE_FAILED")]
    [InlineData("REMOVED")]
    public void BeheerdePostgres_AanhoudendeToestandenZijnUitval(string ruweStatus)
        => DatabaseUitvalCore.BepaalBeheerdePostgresBeschikbaarheid(ruweStatus)
            .Should().Be(DatabaseBeschikbaarheid.Uitgevallen);

    [Theory]
    [InlineData("COMING_UP")]
    [InlineData("RESTARTING")]
    [InlineData("RESTORING")]
    [InlineData("UPGRADING")]
    [InlineData("RESIZING")]
    [InlineData("UNKNOWN")]
    [InlineData("EEN_STATUS_DIE_NOG_NIET_BESTOND")]
    [InlineData("")]
    [InlineData(null)]
    public void BeheerdePostgres_TijdelijkeOfOnbekendeToestandIsOnbepaald(string? ruweStatus)
        => DatabaseUitvalCore.BepaalBeheerdePostgresBeschikbaarheid(ruweStatus)
            .Should().Be(DatabaseBeschikbaarheid.Onbepaald,
                "een onbekende waarde mag geen URGENT-mail veroorzaken én geen lopende melding wissen");

    [Fact]
    public void BeheerdePostgres_StatusIsHoofdletterOngevoeligEnNegeertSpaties()
        => DatabaseUitvalCore.BepaalBeheerdePostgresBeschikbaarheid("  inactive  ")
            .Should().Be(DatabaseBeschikbaarheid.Uitgevallen);

    // ---------------------------------------------------------------------------------------
    // Beoordeel — beschikbaar / onbepaald
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Beschikbaar_ZonderOpenstaandeMelding_DoetNiets()
        => DatabaseUitvalCore.Beoordeel(
                Status("Online", DatabaseBeschikbaarheid.Beschikbaar), null, Nu,
                DatabaseUitvalCore.MinimaleUitvalVoorMelding)
            .Actie.Should().Be(DatabaseUitvalActie.GeenActie);

    [Fact]
    public void Beschikbaar_MetOpenstaandeMelding_WistDieRegistratie()
        => DatabaseUitvalCore.Beoordeel(
                Status("Online", DatabaseBeschikbaarheid.Beschikbaar), Nu.AddDays(-1), Nu,
                DatabaseUitvalCore.MinimaleUitvalVoorMelding)
            .Actie.Should().Be(DatabaseUitvalActie.WisRegistratie);

    /// <summary>
    /// Een tussentoestand mag geen van beide kanten op beslissen. Wél melden zou ruis opleveren bij
    /// elke herstart; wél wissen zou een lopende, meerdaagse uitvalmelding resetten zodra het
    /// platform één keer "RESTARTING" rapporteert — en dan begint de throttle weer van voren af aan.
    /// </summary>
    [Fact]
    public void Onbepaald_MetOpenstaandeMelding_WistDieRegistratieNiet()
    {
        var besluit = DatabaseUitvalCore.Beoordeel(
            Status("RESTARTING", DatabaseBeschikbaarheid.Onbepaald), Nu.AddDays(-2), Nu,
            DatabaseUitvalCore.MinimaleUitvalVoorMeldingBeheerdePostgres);

        besluit.Actie.Should().Be(DatabaseUitvalActie.GeenActie);
    }

    // ---------------------------------------------------------------------------------------
    // Beoordeel — uitval, drempel en herhaling
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Uitgevallen_ZonderStarttijdstip_MeldtNiet()
    {
        var besluit = DatabaseUitvalCore.Beoordeel(
            Status("Paused", DatabaseBeschikbaarheid.Uitgevallen, sinds: null), null, Nu,
            DatabaseUitvalCore.MinimaleUitvalVoorMelding);

        besluit.Actie.Should().Be(DatabaseUitvalActie.GeenActie,
            "zonder duur liever stil dan een fout-positief op een normale auto-pause");
    }

    [Fact]
    public void Uitgevallen_KorterDanDrempel_MeldtNiet()
    {
        var besluit = DatabaseUitvalCore.Beoordeel(
            Status("Paused", DatabaseBeschikbaarheid.Uitgevallen, Nu.AddHours(-1)), null, Nu,
            DatabaseUitvalCore.MinimaleUitvalVoorMelding);

        besluit.Actie.Should().Be(DatabaseUitvalActie.GeenActie);
    }

    /// <summary>
    /// Exact op de drempel telt als "lang genoeg". Zou dit een strikte groter-dan zijn, dan viel een
    /// uitval van precies zes uur door de mazen — en dat is juist de randwaarde waar een dagelijkse
    /// timer met vaste schedule op uitkomt.
    /// </summary>
    [Fact]
    public void Uitgevallen_PreciesOpDrempel_Meldt()
    {
        var besluit = DatabaseUitvalCore.Beoordeel(
            Status("Paused", DatabaseBeschikbaarheid.Uitgevallen,
                Nu - DatabaseUitvalCore.MinimaleUitvalVoorMelding),
            null, Nu, DatabaseUitvalCore.MinimaleUitvalVoorMelding);

        besluit.Actie.Should().Be(DatabaseUitvalActie.Melden);
        besluit.UitvalDuur.Should().Be(DatabaseUitvalCore.MinimaleUitvalVoorMelding);
    }

    [Fact]
    public void Uitgevallen_LangerDanDrempel_MeldtMetDuur()
    {
        var besluit = DatabaseUitvalCore.Beoordeel(
            Status("Paused", DatabaseBeschikbaarheid.Uitgevallen, Nu.AddHours(-10)), null, Nu,
            DatabaseUitvalCore.MinimaleUitvalVoorMelding);

        besluit.Actie.Should().Be(DatabaseUitvalActie.Melden);
        besluit.UitvalDuur.Should().Be(TimeSpan.FromHours(10));
    }

    [Fact]
    public void Uitgevallen_MetRecenteMelding_HerhaaltNiet()
    {
        var besluit = DatabaseUitvalCore.Beoordeel(
            Status("Paused", DatabaseBeschikbaarheid.Uitgevallen, Nu.AddHours(-30)), Nu.AddHours(-5), Nu,
            DatabaseUitvalCore.MinimaleUitvalVoorMelding);

        besluit.Actie.Should().Be(DatabaseUitvalActie.GeenActie);
    }

    /// <summary>
    /// De "dagelijkse herinnering"-eigenschap: tijdens een meerdaagse uitval moet er opnieuw gemeld
    /// worden zodra het herhalingsvenster verstreken is.
    /// </summary>
    [Fact]
    public void Uitgevallen_MetVerlopenMelding_MeldtOpnieuw()
    {
        var besluit = DatabaseUitvalCore.Beoordeel(
            Status("Paused", DatabaseBeschikbaarheid.Uitgevallen, Nu.AddDays(-3)), Nu.AddHours(-25), Nu,
            DatabaseUitvalCore.MinimaleUitvalVoorMelding);

        besluit.Actie.Should().Be(DatabaseUitvalActie.Melden);
    }

    [Fact]
    public void Uitgevallen_PreciesOpHerhalingsinterval_MeldtOpnieuw()
    {
        var besluit = DatabaseUitvalCore.Beoordeel(
            Status("Paused", DatabaseBeschikbaarheid.Uitgevallen, Nu.AddDays(-3)),
            Nu - DatabaseUitvalCore.MinimaleHerhalingsinterval, Nu,
            DatabaseUitvalCore.MinimaleUitvalVoorMelding);

        besluit.Actie.Should().Be(DatabaseUitvalActie.Melden);
    }

    /// <summary>
    /// Het tier-verschil in één test: met de Postgres-drempel telt een uitval die zojuist is
    /// waargenomen al mee, met de Azure SQL-drempel niet. Dat is geen detail maar het hele punt van
    /// twee aparte constanten — zie de toelichting bij beide in <see cref="DatabaseUitvalCore"/>.
    /// </summary>
    [Fact]
    public void ZelfdeUitval_AndereDrempel_AnderBesluit()
    {
        var zojuist = Status("INACTIVE", DatabaseBeschikbaarheid.Uitgevallen, Nu);

        DatabaseUitvalCore.Beoordeel(zojuist, null, Nu,
                DatabaseUitvalCore.MinimaleUitvalVoorMeldingBeheerdePostgres)
            .Actie.Should().Be(DatabaseUitvalActie.Melden);

        DatabaseUitvalCore.Beoordeel(zojuist, null, Nu, DatabaseUitvalCore.MinimaleUitvalVoorMelding)
            .Actie.Should().Be(DatabaseUitvalActie.GeenActie);
    }

    // ---------------------------------------------------------------------------------------
    // Eerste waarneming — voor een platform zonder eigen uitval-tijdstempel
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void EersteWaarneming_NogNietsVastgelegd_GebruiktNuEnMoetVastleggen()
        => DatabaseUitvalCore.BepaalUitvalStart(null, Nu).Should().Be((Nu, true));

    [Fact]
    public void EersteWaarneming_AlVastgelegd_HoudtHetOudsteTijdstipAan()
    {
        var eerder = Nu.AddDays(-2);

        DatabaseUitvalCore.BepaalUitvalStart(eerder, Nu).Should().Be((eerder, false),
            "anders zou elke nieuwe run de duur terugzetten naar nul en nooit de drempel halen");
    }

    // ---------------------------------------------------------------------------------------
    // Noodmailtekst
    // ---------------------------------------------------------------------------------------

    private static DatabaseUitvalMeldingContext Context(TimeSpan duur) => new(
        UitvalDuur: duur,
        TijdstipControleLokaal: new DateTime(2026, 8, 30, 14, 5, 0, DateTimeKind.Unspecified),
        StatusOmschrijving: "gepauzeerd",
        DuurHerkomst: "het tijdstip van het platform",
        BronOmschrijving: "de statuscontrole van het platform",
        VermoedelijkeOorzaak: "de maandlimiet is bereikt",
        ControleStappen: ["Eerste stap", "Tweede stap"]);

    [Fact]
    public void Noodmail_BevatDuurTijdstipHerkomstOorzaakEnStappen()
    {
        var body = DatabaseUitvalCore.BouwNoodmailBody(Context(TimeSpan.FromHours(14)));

        body.Should().Contain("al circa 14 uur gepauzeerd");
        body.Should().Contain("30-08-2026 14:05");
        body.Should().Contain("het tijdstip van het platform");
        body.Should().Contain("de maandlimiet is bereikt");
        body.Should().Contain("- Eerste stap");
        body.Should().Contain("- Tweede stap");
        body.Should().Contain("20 uur herhaald");
    }

    /// <summary>
    /// Bij een net waargenomen uitval is de duur bijna nul. "al circa 0 uur" leest als een fout in de
    /// melding zelf, en dat is precies het moment waarop een ontvanger hem niet serieus neemt — op de
    /// Postgres-tier is dat bovendien de normale eerste melding, niet een randgeval.
    /// </summary>
    [Fact]
    public void Noodmail_BijBijnaNulDuur_SchrijftGeenNulUur()
    {
        var body = DatabaseUitvalCore.BouwNoodmailBody(Context(TimeSpan.Zero));

        body.Should().Contain("sinds kort gepauzeerd");
        body.Should().NotContain("0 uur gepauzeerd");
    }

    [Fact]
    public void Noodmail_ZonderControlestappen_LaatDieSectieWeg()
    {
        var body = DatabaseUitvalCore.BouwNoodmailBody(
            Context(TimeSpan.FromHours(3)) with { ControleStappen = [] });

        body.Should().NotContain("Controleer:");
    }

    [Fact]
    public void Noodmail_BevatGeenOntvangerOfAndereIdentificerendeGegevens()
    {
        var body = DatabaseUitvalCore.BouwNoodmailBody(Context(TimeSpan.FromHours(9)));

        body.Should().NotContain("@", "een noodmailtekst hoort geen adres te bevatten");
    }
}
