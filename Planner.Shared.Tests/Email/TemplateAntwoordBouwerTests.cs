using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>
/// Takkeuze van <see cref="TemplateAntwoordBouwer"/> (#1627) los van een databasetier: een opnemende bron
/// laat zien welke tak is gekozen en met welke waarden. De tekstuitkomst per tak staat in de
/// karakterisatietests van beide tiers.
/// </summary>
public class TemplateAntwoordBouwerTests
{
    private sealed class OpnemendeBron : ITemplateAntwoordBron
    {
        public List<string> Aanroepen { get; } = new();
        public HashSet<string> Sjablonen { get; } = new();
        public int Validaties { get; private set; }

        /// <summary>De placeholders en het dagdeel waarmee per sleutel naar een sjabloon is gevraagd (#1780).</summary>
        public Dictionary<string, IDictionary<string, string>?> Placeholders { get; } = new();
        public string? LaatsteDagdeel { get; private set; }

        public BerichtClassificatie Classificatie { get; } = new();

        public IAntwoordWedstrijd? LeesWedstrijd(JToken? wedstrijd) => wedstrijd is JObject o
            ? new TestWedstrijd
            {
                Wedstrijd = o["Wedstrijd"]?.ToString() ?? "",
                Datum = o["Datum"]?.ToString() ?? "",
                AanvangsTijd = o["AanvangsTijd"]?.ToString() ?? "",
                VeldNaam = o["VeldNaam"]?.ToString()
            }
            : null;

        public IAntwoordBeschikbaarheid LeesBeschikbaarheid(JToken? beschikbaarheid) => new TestBeschikbaarheid
        {
            Beschikbaar = beschikbaarheid?["Beschikbaar"]?.ToObject<bool>() ?? false,
            GecontroleerdDagdeel = beschikbaarheid?["GecontroleerdDagdeel"]?.ToString()
        };

        private (string onderwerp, string body) Gekozen(string naam)
        {
            Aanroepen.Add(naam);
            return (naam, naam);
        }

        public void ValideerWedstrijd(JToken? wedstrijd)
        {
            Validaties++;
            if (wedstrijd is { Type: JTokenType.String })
                throw new Newtonsoft.Json.JsonSerializationException("ongeldige wedstrijd-DTO");
        }

        public (string onderwerp, string body) WedstrijdAlIngepland(JToken? wedstrijd) => Gekozen("wedstrijdAlIngepland");
        public (string onderwerp, string body) TeamOnbekend(string? tegenstander) => Gekozen($"teamOnbekend:{tegenstander ?? "null"}");
        public (string onderwerp, string body) DatumOnbekend() => Gekozen("datumOnbekend");

        public (string onderwerp, string body) MultiDatum(IReadOnlyList<(string datum, JToken? response)> resultaten)
            => Gekozen($"multiDatum:{string.Join(",", resultaten.Select(r => r.datum + (r.response == null ? "-" : "+")))}");

        public Task<(string onderwerp, string body)> BeschikbaarheidStandaardAsync(string plannerResponseJson)
            => Task.FromResult(Gekozen("beschikbaarheidStandaard"));

        public (string onderwerp, string body) HerplanTeLaat(JToken? wedstrijd, int deadlineDagen, int dagenTotWedstrijd)
            => Gekozen($"herplanTeLaat:{deadlineDagen}:{dagenTotWedstrijd}");

        public (string onderwerp, string body) VerzetZonderDatum(JToken? wedstrijd, List<string> vrijeZaterdagen)
            => Gekozen($"verzetZonderDatum:{vrijeZaterdagen.Count}");

        public (string onderwerp, string body) HerplanGewensteDatum(JToken? wedstrijd, string? gewensteDatum, JToken? beschikbaarheid)
            => Gekozen($"gewensteDatum:{gewensteDatum}");

        public (string onderwerp, string body) HerplanStandaard(JToken? wedstrijd, JToken? herplanOpties) => Gekozen("herplanStandaard");

        public Task<(string onderwerp, string body)?> ProbeerSjabloonAsync(
            string sleutel, IDictionary<string, string>? extraPlaceholders = null, string? gecontroleerdDagdeel = null)
        {
            Aanroepen.Add($"sjabloon?{sleutel}");
            Placeholders[sleutel] = extraPlaceholders;
            LaatsteDagdeel = gecontroleerdDagdeel;
            return Task.FromResult<(string onderwerp, string body)?>(Sjablonen.Contains(sleutel) ? ($"override:{sleutel}", "") : null);
        }

        public (string onderwerp, string body) TeamContactStandaard() => Gekozen("teamContactStandaard");
        public (string onderwerp, string body) BevestigingStandaard() => Gekozen("bevestigingStandaard");
        public (string onderwerp, string body) BuitenScopeStandaard() => Gekozen("buitenScopeStandaard");
    }

    private static async Task<(string onderwerp, List<string> aanroepen)> Bouw(
        string type, string json, params string[] sjablonen)
    {
        var bron = new OpnemendeBron();
        foreach (var s in sjablonen) bron.Sjablonen.Add(s);
        var (onderwerp, _) = await TemplateAntwoordBouwer.BouwAsync(type, json, bron, null);
        return (onderwerp, bron.Aanroepen);
    }

    [Theory]
    [InlineData("{\"wedstrijdAlIngepland\":true,\"teamOnbekend\":true,\"datumOnbekend\":true,\"multiDatum\":true,\"resultaten\":[]}", "wedstrijdAlIngepland")]
    [InlineData("{\"teamOnbekend\":true,\"datumOnbekend\":true,\"multiDatum\":true,\"resultaten\":[]}", "teamOnbekend:null")]
    [InlineData("{\"datumOnbekend\":true,\"multiDatum\":true,\"resultaten\":[]}", "datumOnbekend")]
    [InlineData("{\"multiDatum\":true,\"resultaten\":[{\"datum\":\"2026-11-14\",\"response\":{}},{\"datum\":\"2026-11-21\"}]}", "multiDatum:2026-11-14+,2026-11-21-")]
    [InlineData("{\"wedstrijdAlIngepland\":false,\"teamOnbekend\":false}", "beschikbaarheidStandaard")]
    public async Task Beschikbaarheid_EersteGeldendeVlagWint_AndersStandaard(string json, string verwacht)
    {
        var (onderwerp, aanroepen) = await Bouw("BeschikbaarheidCheck", json, "beschikbaarheid_check");

        onderwerp.Should().Be(verwacht);
        // Sinds #1780 vraagt een tak eerst naar zijn eigen sjabloon; daarna volgt de ingebouwde tekst.
        aanroepen.Last().Should().Be(verwacht);
    }

    [Fact]
    public async Task Beschikbaarheid_TeamOnbekend_GeeftTegenstanderUitResponseDoor()
    {
        var (onderwerp, _) = await Bouw("BeschikbaarheidCheck", "{\"teamOnbekend\":true,\"tegenstander\":\"Gast JO13-1\"}");

        onderwerp.Should().Be("teamOnbekend:Gast JO13-1");
    }

    [Theory]
    [InlineData("{\"herplanTeLaat\":true,\"verzetZonderDatum\":true,\"gewensteDatum\":\"x\",\"beschikbaarheid\":{}}", "herplanTeLaat:8:0")]
    [InlineData("{\"herplanTeLaat\":true,\"deadlineDagen\":14,\"dagenTotWedstrijd\":5}", "herplanTeLaat:14:5")]
    [InlineData("{\"verzetZonderDatum\":true,\"gewensteDatum\":\"x\",\"beschikbaarheid\":{}}", "verzetZonderDatum:0")]
    [InlineData("{\"verzetZonderDatum\":true,\"vrijeZaterdagen\":[\"a\",\"b\"]}", "verzetZonderDatum:2")]
    [InlineData("{\"gewensteDatum\":\"2026-11-28\",\"beschikbaarheid\":{}}", "gewensteDatum:2026-11-28")]
    public async Task Herplan_EersteGeldendeTakWint_MetStandaardwaarden(string json, string verwacht)
    {
        var (onderwerp, aanroepen) = await Bouw("HerplanVerzoek", json, "herplan_verzoek");

        onderwerp.Should().Be(verwacht);
        aanroepen.Should().Equal(verwacht);
    }

    [Fact]
    public async Task Herplan_GewensteDatumZonderBeschikbaarheid_ValtDoorNaarSjabloonOfStandaard()
    {
        var (metSjabloon, a1) = await Bouw("HerplanVerzoek", "{\"gewensteDatum\":\"x\"}", "herplan_verzoek");
        var (zonderSjabloon, a2) = await Bouw("HerplanVerzoek", "{\"gewensteDatum\":\"x\"}");

        metSjabloon.Should().Be("override:herplan_verzoek");
        a1.Should().Equal("sjabloon?herplan_verzoek");
        zonderSjabloon.Should().Be("herplanStandaard");
        a2.Should().Equal("sjabloon?herplan_verzoek", "herplanStandaard");
    }

    [Theory]
    [InlineData("TeamContactOpvragen", "team_contact_opvragen", "teamContactStandaard")]
    [InlineData("Bevestiging", "bevestiging", "bevestigingStandaard")]
    [InlineData("BuitenScope", "buiten_scope", "buitenScopeStandaard")]
    [InlineData("OnbekendType", "buiten_scope", "buitenScopeStandaard")]
    public async Task OverigeTypen_SjabloonHeeftVoorrangOpStandaard(string type, string sleutel, string standaard)
    {
        var (override_, a1) = await Bouw(type, "{}", sleutel);
        var (zonder, a2) = await Bouw(type, "{}");

        override_.Should().Be("override:" + sleutel);
        a1.Should().Equal("sjabloon?" + sleutel);
        zonder.Should().Be(standaard);
        a2.Should().Equal("sjabloon?" + sleutel, standaard);
    }

    [Fact]
    public async Task OverigeTypen_ParsenHetPlannerresponseNiet()
    {
        var (onderwerp, _) = await Bouw("Bevestiging", "geen json");

        onderwerp.Should().Be("bevestigingStandaard");
    }

    [Fact]
    public async Task Beschikbaarheid_OngeldigeJson_GooitWatDeTierVoorheenDeed()
    {
        var act = () => Bouw("BeschikbaarheidCheck", "geen json");

        await act.Should().ThrowAsync<Newtonsoft.Json.JsonReaderException>();
    }

    // Foutpad (#1627, Codex ronde 1 P2): de wedstrijd-DTO wordt bij herplan direct na het parsen gevalideerd,
    // vóór takkeuze en sjabloonophaling; een ongeldige DTO breekt af zonder sjabloonophaling (met en zonder override).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Herplan_OngeldigeWedstrijd_FaaltVoorTakkeuzeEnSjabloonophaling(bool metSjabloon)
    {
        var bron = new OpnemendeBron();
        if (metSjabloon) bron.Sjablonen.Add("herplan_verzoek");

        var act = () => TemplateAntwoordBouwer.BouwAsync("HerplanVerzoek", "{\"wedstrijd\":\"geen-object\"}", bron, null);

        await act.Should().ThrowAsync<Newtonsoft.Json.JsonSerializationException>();
        bron.Validaties.Should().Be(1);
        bron.Aanroepen.Should().BeEmpty();
    }

    // ── #1780: elk antwoord heeft een eigen sjabloonsleutel ──

    private const string Wedstrijd =
        "\"wedstrijd\":{\"Wedstrijd\":\"JO13-1 - Gast\",\"Datum\":\"2026-11-14\",\"AanvangsTijd\":\"10:00\",\"VeldNaam\":\"Veld 1\"}";

    [Theory]
    [InlineData("BeschikbaarheidCheck", "{\"wedstrijdAlIngepland\":true," + Wedstrijd + "}", "wedstrijd_al_ingepland")]
    [InlineData("BeschikbaarheidCheck", "{\"teamOnbekend\":true,\"tegenstander\":\"Gast\"}", "team_onbekend")]
    [InlineData("BeschikbaarheidCheck", "{\"datumOnbekend\":true}", "datum_onbekend")]
    [InlineData("BeschikbaarheidCheck", "{\"multiDatum\":true,\"resultaten\":[{\"datum\":\"2026-11-14\",\"response\":{}}]}", "multi_datum")]
    [InlineData("HerplanVerzoek", "{\"herplanTeLaat\":true," + Wedstrijd + "}", "herplan_te_laat")]
    [InlineData("HerplanVerzoek", "{\"verzetZonderDatum\":true," + Wedstrijd + "}", "verzet_zonder_datum")]
    [InlineData("HerplanVerzoek", "{\"gewensteDatum\":\"2026-11-21\",\"beschikbaarheid\":{}," + Wedstrijd + "}", "herplan_gewenste_datum")]
    public async Task ElkAntwoord_EigenSjabloonVervangtDeIngebouwdeTekst(string type, string json, string sleutel)
    {
        var (onderwerp, aanroepen) = await Bouw(type, json, sleutel);

        onderwerp.Should().Be("override:" + sleutel);
        aanroepen.Should().Equal("sjabloon?" + sleutel);
    }

    [Theory]
    [InlineData("BeschikbaarheidCheck", "{\"wedstrijdAlIngepland\":true}", "wedstrijdAlIngepland")]
    [InlineData("HerplanVerzoek", "{\"herplanTeLaat\":true}", "herplanTeLaat:8:0")]
    [InlineData("HerplanVerzoek", "{\"verzetZonderDatum\":true}", "verzetZonderDatum:0")]
    [InlineData("HerplanVerzoek", "{\"gewensteDatum\":\"2026-11-21\",\"beschikbaarheid\":{}}", "gewensteDatum:2026-11-21")]
    public async Task AntwoordDatEenWedstrijdNodigHeeft_ZonderWedstrijd_GeeftDeIngebouwdeUitleg(string type, string json, string standaard)
    {
        var alleSjablonen = AntwoordSjabloonCatalogus.Alle.Select(i => i.Sleutel).ToArray();

        var (onderwerp, aanroepen) = await Bouw(type, json, alleSjablonen);

        onderwerp.Should().Be(standaard);
        aanroepen.Should().Equal(standaard);
    }

    [Fact]
    public async Task Placeholders_WedstrijdAlIngepland_BevatDeWedstrijdgegevens()
    {
        var bron = new OpnemendeBron();
        bron.Sjablonen.Add("wedstrijd_al_ingepland");

        await TemplateAntwoordBouwer.BouwAsync("BeschikbaarheidCheck", "{\"wedstrijdAlIngepland\":true," + Wedstrijd + "}", bron, null);

        var p = bron.Placeholders["wedstrijd_al_ingepland"]!;
        p["wedstrijd"].Should().Be("JO13-1 - Gast");
        p["wedstrijd_datum"].Should().Be("zaterdag 14 november 2026");
        p["wedstrijd_aanvangstijd"].Should().Be("10:00");
        p["wedstrijd_veld"].Should().Be("Veld 1");
    }

    [Fact]
    public async Task Placeholders_HerplanTeLaat_BevatDeadlineEnDagenTotDeWedstrijd()
    {
        var bron = new OpnemendeBron();
        bron.Sjablonen.Add("herplan_te_laat");

        await TemplateAntwoordBouwer.BouwAsync("HerplanVerzoek",
            "{\"herplanTeLaat\":true,\"deadlineDagen\":14,\"dagenTotWedstrijd\":5," + Wedstrijd + "}", bron, null);

        var p = bron.Placeholders["herplan_te_laat"]!;
        p["deadline_dagen"].Should().Be("14");
        p["dagen_tot_wedstrijd"].Should().Be("5");
        p["wedstrijd"].Should().Be("JO13-1 - Gast");
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "De KNVB-speeldagenkalender is als bijlage toegevoegd.")]
    public async Task Placeholders_VerzetZonderDatum_ToontDeBijlagezinAlleenMetBijlage(bool metBijlage, string verwacht)
    {
        var bron = new OpnemendeBron();
        bron.Classificatie.VoegKnvbPdfBijlageToe = metBijlage;
        bron.Sjablonen.Add("verzet_zonder_datum");

        await TemplateAntwoordBouwer.BouwAsync("HerplanVerzoek",
            "{\"verzetZonderDatum\":true,\"vrijeZaterdagen\":[\"2026-11-21\",\"2026-11-28\"]," + Wedstrijd + "}", bron, null);

        var p = bron.Placeholders["verzet_zonder_datum"]!;
        p["bijlage_zin"].Should().Be(verwacht);
        p["vrije_zaterdagen"].Should().Contain("- zaterdag 21 november 2026").And.Contain("- zaterdag 28 november 2026");
    }

    [Fact]
    public async Task Placeholders_HerplanGewensteDatum_GeeftGewensteDatumMogelijkhedenEnDagdeel()
    {
        var bron = new OpnemendeBron();
        bron.Sjablonen.Add("herplan_gewenste_datum");

        await TemplateAntwoordBouwer.BouwAsync("HerplanVerzoek",
            "{\"gewensteDatum\":\"2026-11-21\",\"beschikbaarheid\":{\"Beschikbaar\":false,\"GecontroleerdDagdeel\":\"ochtend\"}," + Wedstrijd + "}", bron, null);

        var p = bron.Placeholders["herplan_gewenste_datum"]!;
        p["gewenste_datum"].Should().Be("zaterdag 21 november 2026");
        p["mogelijkheden"].Should().Contain("zaterdag 21 november 2026");
        bron.LaatsteDagdeel.Should().Be("ochtend");
    }

    [Fact]
    public async Task Placeholders_MultiDatum_BevatEenOverzichtPerDatum()
    {
        var bron = new OpnemendeBron();
        bron.Sjablonen.Add("multi_datum");

        await TemplateAntwoordBouwer.BouwAsync("BeschikbaarheidCheck",
            "{\"multiDatum\":true,\"resultaten\":[{\"datum\":\"2026-11-14\",\"response\":{}},{\"datum\":\"2026-11-21\",\"response\":{}}]}", bron, null);

        var overzicht = bron.Placeholders["multi_datum"]!["overzicht"];
        overzicht.Should().Contain("zaterdag 14 november 2026").And.Contain("zaterdag 21 november 2026");
    }

    [Theory]
    [InlineData("{\"teamOnbekend\":true,\"tegenstander\":\"Gast JO13-1\"}", true)]
    [InlineData("{\"teamOnbekend\":true}", false)]
    public async Task Placeholders_TeamOnbekend_GebruiktDeTegenstanderUitHetPlannerresponse(string json, bool verwachtOverschrijving)
    {
        var bron = new OpnemendeBron();
        bron.Sjablonen.Add("team_onbekend");

        await TemplateAntwoordBouwer.BouwAsync("BeschikbaarheidCheck", json, bron, null);

        var p = bron.Placeholders["team_onbekend"]!;
        p.ContainsKey("tegenstander").Should().Be(verwachtOverschrijving);
        if (verwachtOverschrijving) p["tegenstander"].Should().Be("Gast JO13-1");
    }

    [Fact]
    public async Task Placeholders_HerplanStandaard_KrijgtDeWedstrijdgegevens()
    {
        var bron = new OpnemendeBron();
        bron.Sjablonen.Add("herplan_verzoek");

        await TemplateAntwoordBouwer.BouwAsync("HerplanVerzoek", "{" + Wedstrijd + "}", bron, null);

        bron.Placeholders["herplan_verzoek"]!["wedstrijd_veld"].Should().Be("Veld 1");
    }
}
