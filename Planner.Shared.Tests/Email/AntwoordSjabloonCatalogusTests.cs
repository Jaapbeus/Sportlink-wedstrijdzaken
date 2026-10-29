using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>
/// De catalogus van overschrijfbare antwoorden (#1780) is de enige bron voor de beheerpagina én de verwerker.
/// Deze tests leggen vast dat hij klopt met de placeholders die de generator werkelijk levert, en dat de
/// standaardtekst in het formulier dezelfde tekst oplevert als het ingebouwde antwoord.
/// </summary>
[Collection(BerichtAntwoordCollectie.Naam)]
public class AntwoordSjabloonCatalogusTests
{
    private static readonly TestAfzender Afzender = new() { Onderwerp = "Oefenwedstrijd" };
    private static readonly TestInstellingen Instellingen = new() { EmailVoetnoot = "Testomgeving", CoordinatorNaam = "Jan de Vries" };

    private static readonly TestWedstrijd Wedstrijd = new()
    {
        Wedstrijd = "JO13-1 - Gast", Datum = "2026-11-14", AanvangsTijd = "10:00", VeldNaam = "Veld 1"
    };

    private static BerichtClassificatie Classificatie(bool bijlage = false) => new()
    {
        Type = VerzoekType.HerplanVerzoek, Datum = "2026-11-14", TeamNaam = "JO13-1", Tegenstander = "Gast",
        AanvangsTijd = "10:00", VoegKnvbPdfBijlageToe = bijlage
    };

    private static TestBeschikbaarheid Beschikbaar() => new()
    {
        Beschikbaar = true,
        BeschikbareVensters = [new TestVenster { VeldNaam = "Veld 1", Van = "09:00", Tot = "12:00", VeldType = "kunstgras" }]
    };

    private static Dictionary<string, string>? Placeholders(string sleutel) => sleutel switch
    {
        AntwoordSjabloonSleutels.WedstrijdAlIngepland or AntwoordSjabloonSleutels.HerplanVerzoek
            => BerichtAntwoordGenerator.WedstrijdPlaceholders(Wedstrijd),
        AntwoordSjabloonSleutels.TeamOnbekend => BerichtAntwoordGenerator.TeamOnbekendPlaceholders("Gast"),
        AntwoordSjabloonSleutels.MultiDatum => BerichtAntwoordGenerator.MultiDatumPlaceholders(
            [("2026-11-14", Beschikbaar()), ("2026-11-21", new TestBeschikbaarheid())], Classificatie()),
        AntwoordSjabloonSleutels.HerplanTeLaat => BerichtAntwoordGenerator.HerplanTeLaatPlaceholders(Wedstrijd, 14, 5),
        AntwoordSjabloonSleutels.VerzetZonderDatum
            => BerichtAntwoordGenerator.VerzetZonderDatumPlaceholders(Wedstrijd, ["2026-11-21", "2026-11-28"], true),
        AntwoordSjabloonSleutels.HerplanGewensteDatum
            => BerichtAntwoordGenerator.HerplanGewensteDatumPlaceholders(Wedstrijd, "2026-11-21", Beschikbaar()),
        _ => null,
    };

    [Fact]
    public void Catalogus_HeeftPrecies_DeSleutelsVanAntwoordSjabloonSleutels_ZonderDubbelen()
    {
        var constanten = typeof(AntwoordSjabloonSleutels).GetFields()
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        var sleutels = AntwoordSjabloonCatalogus.Alle.Select(i => i.Sleutel).ToList();

        sleutels.Should().BeEquivalentTo(constanten);
        sleutels.Should().OnlyHaveUniqueItems().And.HaveCount(12);
        sleutels.Should().OnlyContain(s => s.All(c => char.IsAsciiLetterLower(c) || c == '_'));
    }

    [Fact]
    public void Catalogus_VoorElkeSleutel_UitlegGroepEnStandaardtekstZijnGevuld()
    {
        foreach (var info in AntwoordSjabloonCatalogus.Alle)
        {
            info.Groep.Should().NotBeNullOrWhiteSpace(info.Sleutel);
            info.Wanneer.Should().NotBeNullOrWhiteSpace(info.Sleutel);
            info.StandaardBody.Should().NotBeNullOrWhiteSpace(info.Sleutel);
        }
    }

    [Fact]
    public void Catalogus_DeclareertPrecies_DePlaceholdersDieDeGeneratorLevert()
    {
        foreach (var info in AntwoordSjabloonCatalogus.Alle)
        {
            var geleverd = Placeholders(info.Sleutel)?.Keys
                .Except(AntwoordSjabloonCatalogus.BasisPlaceholders, StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k).ToList() ?? new List<string>();

            info.ExtraPlaceholders.OrderBy(k => k).Should().Equal(geleverd, $"sleutel {info.Sleutel}");
        }
    }

    [Fact]
    public void StandaardTekst_GebruiktAlleenBekendePlaceholders_EnLaatGeenAccoladesStaan()
    {
        foreach (var info in AntwoordSjabloonCatalogus.Alle)
        {
            var (onderwerp, body) = BerichtAntwoordGenerator.BouwAangepasteAntwoord(
                new TestSjabloon(info.Sleutel, info.StandaardOnderwerp, info.StandaardBody),
                Classificatie(), Afzender, Instellingen, Placeholders(info.Sleutel));

            onderwerp.Should().NotContain("{{", info.Sleutel);
            body.Should().NotContain("{{", info.Sleutel);
        }
    }

    [Fact]
    public void LeegOnderwerp_GeeftReOnderwerpVanDeMail()
    {
        var info = AntwoordSjabloonCatalogus.Zoek(AntwoordSjabloonSleutels.TeamOnbekend)!;

        var (onderwerp, _) = BerichtAntwoordGenerator.BouwAangepasteAntwoord(
            new TestSjabloon(info.Sleutel, info.StandaardOnderwerp, info.StandaardBody),
            Classificatie(), Afzender, Instellingen, Placeholders(info.Sleutel));

        onderwerp.Should().Be("Re: Oefenwedstrijd");
    }

    // De standaardtekst in het formulier moet dezelfde antwoordtekst geven als het ingebouwde antwoord, behalve de
    // aanhef (hangt van het uur af en staat in de eerste regel) en rijen lege regels: het ingebouwde verzet-zonder-
    // datum-antwoord laat na de lijst met zaterdagen een extra lege regel staan.
    private static string ZonderAanhef(string body)
        => System.Text.RegularExpressions.Regex.Replace(body.Split('\n', 2)[1], "\n{3,}", "\n\n");

    private static string UitStandaardtekst(string sleutel, BerichtClassificatie classificatie, string? dagdeel = null)
    {
        var info = AntwoordSjabloonCatalogus.Zoek(sleutel)!;
        return ZonderAanhef(BerichtAntwoordGenerator.BouwAangepasteAntwoord(
            new TestSjabloon(sleutel, info.StandaardOnderwerp, info.StandaardBody),
            classificatie, Afzender, Instellingen, Placeholders(sleutel), dagdeel).body);
    }

    [Fact]
    public void Standaardtekst_WedstrijdAlIngepland_IsGelijkAanHetIngebouwdeAntwoord()
        => UitStandaardtekst(AntwoordSjabloonSleutels.WedstrijdAlIngepland, Classificatie())
            .Should().Be(ZonderAanhef(BerichtAntwoordGenerator.BouwWedstrijdAlIngeplandAntwoord(
                Wedstrijd, Classificatie(), Afzender, Instellingen).body));

    [Fact]
    public void Standaardtekst_TeamOnbekend_IsGelijkAanHetIngebouwdeAntwoord()
        => UitStandaardtekst(AntwoordSjabloonSleutels.TeamOnbekend, Classificatie())
            .Should().Be(ZonderAanhef(BerichtAntwoordGenerator.BouwTeamOnbekendAntwoord(
                "Gast", Classificatie(), Afzender, Instellingen).body));

    [Fact]
    public void Standaardtekst_HerplanTeLaat_IsGelijkAanHetIngebouwdeAntwoord()
        => UitStandaardtekst(AntwoordSjabloonSleutels.HerplanTeLaat, Classificatie())
            .Should().Be(ZonderAanhef(BerichtAntwoordGenerator.BouwHerplanTeLaatAntwoord(
                Wedstrijd, 14, 5, Classificatie(), Afzender, Instellingen).body));

    [Fact]
    public void Standaardtekst_VerzetZonderDatum_MetBijlage_IsGelijkAanHetIngebouwdeAntwoord()
        => UitStandaardtekst(AntwoordSjabloonSleutels.VerzetZonderDatum, Classificatie(bijlage: true))
            .Should().Be(ZonderAanhef(BerichtAntwoordGenerator.BouwVerzetZonderDatumAntwoord(
                Wedstrijd, ["2026-11-21", "2026-11-28"], Classificatie(bijlage: true), Afzender, Instellingen).body));

    [Fact]
    public void Standaardtekst_HerplanGewensteDatum_IsGelijkAanHetIngebouwdeAntwoord()
        => UitStandaardtekst(AntwoordSjabloonSleutels.HerplanGewensteDatum, Classificatie())
            .Should().Be(ZonderAanhef(BerichtAntwoordGenerator.BouwHerplanGewensteDatumAntwoord(
                Wedstrijd, "2026-11-21", Beschikbaar(), Classificatie(), Afzender, Instellingen).body));

    [Fact]
    public void Standaardtekst_MultiDatum_IsGelijkAanHetIngebouwdeAntwoord()
        => UitStandaardtekst(AntwoordSjabloonSleutels.MultiDatum, Classificatie())
            .Should().Be(ZonderAanhef(BerichtAntwoordGenerator.BouwMultiDatumBeschikbaarheidAntwoord(
                [("2026-11-14", Beschikbaar()), ("2026-11-21", new TestBeschikbaarheid())], Classificatie(), Afzender, Instellingen).body));
}
