using Planner.Shared;
using FluentAssertions;
using Newtonsoft.Json;
using SportlinkFunction.Email;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Regressietests voor het reply-beleid (#572, #578).
///
/// Legt de functionele eis van de eigenaar (2026-07-25) vast:
///   • enkele datum, planning mogelijk      → géén automatisch antwoord
///   • enkele datum, planning niet mogelijk → wél antwoord
///   • meerdere datums, gemengd             → wél antwoord
///   • meerdere datums, alles mogelijk      → géén antwoord
///   • meerdere datums, niets mogelijk      → wél antwoord
///
/// De JSON wordt met dezelfde JsonConvert-aanroepen opgebouwd als in BerichtPipeline,
/// zodat een wijziging in serialisatie (bijv. camelCase-instelling) hier opvalt in plaats
/// van stil de policy te breken.
/// </summary>
public class ReplyPolicyTests
{
    private static BerichtClassificatie Beschikbaarheid() =>
        new() { Type = VerzoekType.BeschikbaarheidCheck, Datum = "2026-09-12", TeamNaam = "TEST JO14-1" };

    private static string EnkeleDatumJson(bool beschikbaar) =>
        JsonConvert.SerializeObject(new CheckAvailabilityResponse
        {
            Beschikbaar = beschikbaar,
            Reden = beschikbaar ? null : "Geen beschikbaar veld gevonden.",
            Toewijzing = beschikbaar
                ? new SlotToewijzing { Datum = "2026-09-12", AanvangsTijd = "10:00", EindTijd = "11:45", VeldNummer = 1 }
                : null
        });

    private static string MultiDatumJson(params bool[] beschikbaarPerDatum)
    {
        var resultaten = beschikbaarPerDatum.Select((b, i) => (object)new
        {
            datum = new DateOnly(2026, 9, 7).AddDays(i).ToString("yyyy-MM-dd"),
            response = new CheckAvailabilityResponse
            {
                Beschikbaar = b,
                Reden = b ? null : "Geen beschikbaar veld gevonden."
            }
        }).ToList();
        return JsonConvert.SerializeObject(new { multiDatum = true, resultaten });
    }

    // ── Enkele datum ──

    [Fact]
    public void EnkeleDatum_PlanningMogelijk_GeenAntwoord()
    {
        var besluit = ReplyPolicy.Bepaal(Beschikbaarheid(), EnkeleDatumJson(beschikbaar: true));

        besluit.Actie.Should().Be(ReplyActie.Onderdrukken);
        besluit.MoetVersturen.Should().BeFalse();
    }

    [Fact]
    public void EnkeleDatum_PlanningNietMogelijk_WelAntwoord()
    {
        var besluit = ReplyPolicy.Bepaal(Beschikbaarheid(), EnkeleDatumJson(beschikbaar: false));

        besluit.Actie.Should().Be(ReplyActie.Versturen);
        besluit.Reden.Should().Contain("niet mogelijk");
    }

    [Fact]
    public void EnkeleDatum_TeamConflict_WelAntwoord()
    {
        // Teamconflict levert Beschikbaar = false met een Reden — moet antwoord opleveren
        var json = JsonConvert.SerializeObject(new CheckAvailabilityResponse
        {
            Beschikbaar = false,
            Reden = "TEST JO14-1 heeft al een wedstrijd op 12 september.",
            TeamConflict = new TeamConflictInfo { Wedstrijd = "TEST JO14-1 - Ander JO14-1", AanvangsTijd = "10:00" }
        });

        ReplyPolicy.Bepaal(Beschikbaarheid(), json).MoetVersturen.Should().BeTrue();
    }

    // ── Meerdere datums ──

    [Fact]
    public void MultiDatum_GemengdeUitkomst_WelAntwoord()
    {
        var besluit = ReplyPolicy.Bepaal(Beschikbaarheid(), MultiDatumJson(true, false, true, false));

        besluit.Actie.Should().Be(ReplyActie.Versturen);
        besluit.Reden.Should().Contain("Gemengde uitkomst");
    }

    [Fact]
    public void MultiDatum_AllesMogelijk_GeenAntwoord()
    {
        ReplyPolicy.Bepaal(Beschikbaarheid(), MultiDatumJson(true, true, true))
            .Actie.Should().Be(ReplyActie.Onderdrukken);
    }

    [Fact]
    public void MultiDatum_NietsMogelijk_WelAntwoord()
    {
        ReplyPolicy.Bepaal(Beschikbaarheid(), MultiDatumJson(false, false))
            .Actie.Should().Be(ReplyActie.Versturen);
    }

    [Fact]
    public void MultiDatum_EenDatumMogelijkEnEenNiet_WelAntwoord()
    {
        // Minimale gemengde variant uit de eigenaarseis
        ReplyPolicy.Bepaal(Beschikbaarheid(), MultiDatumJson(true, false))
            .MoetVersturen.Should().BeTrue();
    }

    [Fact]
    public void MultiDatum_ZonderResultaten_WelAntwoord()
    {
        var json = JsonConvert.SerializeObject(new { multiDatum = true, resultaten = new List<object>() });

        ReplyPolicy.Bepaal(Beschikbaarheid(), json).MoetVersturen.Should().BeTrue();
    }

    // ── Bijzondere plannerantwoorden ──

    [Fact]
    public void WedstrijdAlIngepland_WelAntwoord()
    {
        // Informatief: de aanvrager moet weten dat de wedstrijd al staat
        var json = JsonConvert.SerializeObject(new
        {
            wedstrijdAlIngepland = true,
            wedstrijd = new ZoekWedstrijdResponse { Wedstrijd = "TEST JO14-1 - Ander JO14-1", Datum = "2026-09-12" }
        });

        ReplyPolicy.Bepaal(Beschikbaarheid(), json).MoetVersturen.Should().BeTrue();
    }

    [Fact]
    public void TeamOnbekend_WelAntwoord()
    {
        var json = JsonConvert.SerializeObject(new { teamOnbekend = true, tegenstander = "Onbekende Club JO14-1" });

        ReplyPolicy.Bepaal(Beschikbaarheid(), json).MoetVersturen.Should().BeTrue();
    }

    [Fact]
    public void OnleesbareJson_FailOpenNaarAntwoord()
    {
        ReplyPolicy.Bepaal(Beschikbaarheid(), "dit is geen json").MoetVersturen.Should().BeTrue();
    }

    // ── Andere verzoektypes blijven altijd antwoorden ──

    [Theory]
    [InlineData(VerzoekType.HerplanVerzoek)]
    [InlineData(VerzoekType.TeamContactOpvragen)]
    [InlineData(VerzoekType.Bevestiging)]
    public void AndereTypes_AltijdAntwoord(VerzoekType type)
    {
        var classificatie = new BerichtClassificatie { Type = type, Datum = "2026-09-12" };

        // Zelfs met een "wel beschikbaar"-payload blijft het antwoord verplicht:
        // deze types hebben geen planbaarheidsuitkomst maar inhoudelijke informatie.
        ReplyPolicy.Bepaal(classificatie, EnkeleDatumJson(beschikbaar: true))
            .MoetVersturen.Should().BeTrue();
    }

    [Fact]
    public void CamelCaseSerialisatie_WordtOokGelezen()
    {
        // Robuustheid: policy mag niet omvallen als de serialisatie ooit naar camelCase gaat
        ReplyPolicy.Bepaal(Beschikbaarheid(), "{\"beschikbaar\":true}")
            .Actie.Should().Be(ReplyActie.Onderdrukken);
        ReplyPolicy.Bepaal(Beschikbaarheid(), "{\"Beschikbaar\":true}")
            .Actie.Should().Be(ReplyActie.Onderdrukken);
    }
}
