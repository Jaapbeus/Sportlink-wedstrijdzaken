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

        private (string onderwerp, string body) Gekozen(string naam)
        {
            Aanroepen.Add(naam);
            return (naam, naam);
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

        public Task<(string onderwerp, string body)?> ProbeerSjabloonAsync(string sleutel)
        {
            Aanroepen.Add($"sjabloon?{sleutel}");
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
        aanroepen.Should().Equal(verwacht);
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
}
