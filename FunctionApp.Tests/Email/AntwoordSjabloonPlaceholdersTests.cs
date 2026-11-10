using AwesomeAssertions;
using SportlinkFunction.Email;
using SportlinkFunction.Processing;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Eigen sjablonen voor de vaste antwoorden (#1780) via de echte tier-adapter: de placeholders krijgen de waarden uit
/// het plannerresponse, en zonder gevonden wedstrijd blijft de ingebouwde uitleg staan. Het sjabloon komt uit een stub
/// (geen database). De verwachte waarden zijn gelijk aan die van de Postgres-tier (FunctionApp.Postgres.Tests).
/// </summary>
public class AntwoordSjabloonPlaceholdersTests
{
    private const string Wedstrijd =
        "\"wedstrijd\":{\"wedstrijd\":\"Voorbeeld JO13-2 - Gast JO13-1\",\"datum\":\"2026-11-14\",\"aanvangsTijd\":\"10:30\",\"eindTijd\":\"12:00\",\"veldNaam\":\"Veld 1\"}";

    private sealed record Uitkomst(string Onderwerp, string Body, List<string> OpgevraagdeSleutels, string TraceBron);

    private static async Task<Uitkomst> Bouw(
        VerzoekType type, string plannerJson, string? sjabloonBody, bool knvbBijlage = false, string? tegenstander = null)
    {
        var classificatie = new BerichtClassificatie
        {
            Type = type, TeamNaam = "JO13-2", Tegenstander = tegenstander, VoegKnvbPdfBijlageToe = knvbBijlage
        };
        var bericht = new InkomendBericht
        {
            Afzender = "trainer@voorbeeld.nl", AfzenderNaam = "Jan de Vries", Onderwerp = "Oefenwedstrijd", Body = "Kunnen we spelen?"
        };
        var opgevraagd = new List<string>();
        var trace = new TraceBuilder();

        var (onderwerp, body) = await BerichtPipeline.BouwTemplateAntwoord(
            classificatie, plannerJson, bericht, null,
            new ClubAppSettingsSnapshot("ALLSTARS Veldplanner", null, null, null, null), "ALLSTARS", trace,
            sleutel =>
            {
                opgevraagd.Add(sleutel);
                return Task.FromResult<EmailTemplate?>(sjabloonBody == null ? null : new EmailTemplate(sleutel, "", sjabloonBody));
            });

        var bron = trace.Bouw().Stappen.Single(s => s.Code == TraceCodes.Sjabloon).Details["bron"];
        return new Uitkomst(onderwerp, body, opgevraagd, bron);
    }

    [Fact]
    public async Task HerplanTeLaat_EigenSjabloon_KrijgtWedstrijdEnDeadlineUitHetPlannerresponse()
    {
        var uitkomst = await Bouw(VerzoekType.HerplanVerzoek,
            "{\"herplanTeLaat\":true,\"deadlineDagen\":8,\"dagenTotWedstrijd\":3," + Wedstrijd + "}",
            "Over {{dagen_tot_wedstrijd}} dagen: {{wedstrijd}} op {{wedstrijd_datum}} om {{wedstrijd_aanvangstijd}} ({{wedstrijd_veld}}), deadline {{deadline_dagen}}.");

        uitkomst.Body.Should().Contain(
            "Over 3 dagen: Voorbeeld JO13-2 - Gast JO13-1 op zaterdag 14 november 2026 om 10:30 (Veld 1), deadline 8.");
        uitkomst.OpgevraagdeSleutels.Should().Equal("herplan_te_laat");
        uitkomst.TraceBron.Should().Be("database-override");
    }

    [Fact]
    public async Task HerplanTeLaat_ZonderGevondenWedstrijd_BlijftDeIngebouwdeUitlegStaan()
    {
        var uitkomst = await Bouw(VerzoekType.HerplanVerzoek,
            "{\"herplanTeLaat\":true,\"deadlineDagen\":8}", "Eigen tekst {{wedstrijd}}");

        uitkomst.Body.Should().Contain("te laat ingediend").And.NotContain("Eigen tekst");
        uitkomst.OpgevraagdeSleutels.Should().BeEmpty();
        uitkomst.TraceBron.Should().Be("generator");
    }

    [Fact]
    public async Task TeamOnbekend_EigenSjabloon_GebruiktDeTegenstanderUitHetPlannerresponse()
    {
        var uitkomst = await Bouw(VerzoekType.BeschikbaarheidCheck,
            "{\"teamOnbekend\":true,\"tegenstander\":\"Onbekend JO13-1\"}",
            "Welk team speelt tegen {{tegenstander}}?", tegenstander: "Gast JO13-1");

        uitkomst.Body.Should().Contain("Welk team speelt tegen Onbekend JO13-1?");
        uitkomst.OpgevraagdeSleutels.Should().Equal("team_onbekend");
    }

    [Fact]
    public async Task MultiDatum_EigenSjabloon_KrijgtEenOverzichtPerDatum()
    {
        var uitkomst = await Bouw(VerzoekType.BeschikbaarheidCheck,
            "{\"multiDatum\":true,\"resultaten\":[{\"datum\":\"2026-11-14\",\"response\":{\"beschikbaar\":true}},{\"datum\":\"2026-11-21\"}]}",
            "Overzicht:\n{{overzicht}}");

        uitkomst.Body.Should().Contain("Overzicht:\n**zaterdag 14 november 2026:**")
            .And.Contain("**zaterdag 21 november 2026:** Helaas geen veld beschikbaar.");
        uitkomst.OpgevraagdeSleutels.Should().Equal("multi_datum");
    }

    [Theory]
    [InlineData(true, "De KNVB-speeldagenkalender is als bijlage toegevoegd.")]
    [InlineData(false, "")]
    public async Task VerzetZonderDatum_EigenSjabloon_ToontDeVrijeZaterdagenEnDeBijlagezinAlleenMetBijlage(bool bijlage, string zin)
    {
        var uitkomst = await Bouw(VerzoekType.HerplanVerzoek,
            "{\"verzetZonderDatum\":true," + Wedstrijd + ",\"vrijeZaterdagen\":[\"2026-11-14\",\"2026-11-21\"]}",
            "{{vrije_zaterdagen}}\n\n{{bijlage_zin}}", knvbBijlage: bijlage);

        uitkomst.Body.Should().Contain("- zaterdag 14 november 2026\n- zaterdag 21 november 2026");
        if (zin.Length > 0) uitkomst.Body.Should().Contain(zin);
        else uitkomst.Body.Should().NotContain("KNVB-speeldagenkalender");
        uitkomst.OpgevraagdeSleutels.Should().Equal("verzet_zonder_datum");
    }

    [Fact]
    public async Task HerplanGewensteDatum_EigenSjabloon_KrijgtDeMogelijkhedenOpDeGewensteDatum()
    {
        var uitkomst = await Bouw(VerzoekType.HerplanVerzoek,
            "{\"gewensteDatum\":\"2026-11-28\",\"beschikbaarheid\":{\"beschikbaar\":true}," + Wedstrijd + "}",
            "{{wedstrijd}}: {{mogelijkheden}}");

        uitkomst.Body.Should().Contain("Voorbeeld JO13-2 - Gast JO13-1: Op zaterdag 28 november 2026 is er ruimte beschikbaar.");
        uitkomst.OpgevraagdeSleutels.Should().Equal("herplan_gewenste_datum");
    }

    [Fact]
    public async Task ZonderSjabloon_BlijftDeIngebouwdeTekstStaan_EnIsErPerAntwoordEenOpvraag()
    {
        var uitkomst = await Bouw(VerzoekType.BeschikbaarheidCheck,
            "{\"wedstrijdAlIngepland\":true," + Wedstrijd + "}", sjabloonBody: null);

        uitkomst.Body.Should().Contain("staat al ingepland op zaterdag 14 november 2026 om 10:30");
        uitkomst.OpgevraagdeSleutels.Should().Equal("wedstrijd_al_ingepland");
        uitkomst.TraceBron.Should().Be("generator");
    }
}
