using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>Leeracties vanuit de trace en de vergelijking vóór ↔ na (#1568 deel C).</summary>
public class TraceActiesTests
{
    private static TraceStapDto Stap(string code, string uitkomst = "", params (string Sleutel, string Waarde)[] details) => new()
    {
        Code = code,
        Uitkomst = uitkomst,
        Details = details.ToDictionary(d => d.Sleutel, d => d.Waarde)
    };

    [Theory]
    [InlineData("team-herkenning", "Onopgelost")]
    [InlineData("team-herkenning", "MeerdereKandidaten")]
    [InlineData("tegenstander-herkenning", "Onopgelost")]
    public void KoppelTekst_BijNietHerkendTeam_GeeftDeTekst(string code, string bron)
        => TraceActies.KoppelTekst(Stap(code, "", ("bron", bron), ("ruweTekst", "j10-04"))).Should().Be("j10-04");

    [Theory]
    [InlineData("team-herkenning", "ExacteAlias", "j10-04")]
    [InlineData("team-herkenning", "Onopgelost", "")]
    [InlineData("team-herkenning", "Onopgelost", "  ")]
    [InlineData("opponent-team-herkenning", "Onopgelost", "j10-04")]
    [InlineData("classificatie", "Onopgelost", "j10-04")]
    public void KoppelTekst_BijHerkendOfLeegOfAndereStap_GeeftNull(string code, string bron, string tekst)
        => TraceActies.KoppelTekst(Stap(code, "", ("bron", bron), ("ruweTekst", tekst))).Should().BeNull();

    [Theory]
    [InlineData("JO13 [e-mail]")]
    [InlineData("[nummer]")]
    public void KoppelTekst_GemaskeerdeTekst_WordtNietAangebodenOmTeKoppelen(string tekst)
        => TraceActies.KoppelTekst(Stap("team-herkenning", "", ("bron", "Onopgelost"), ("ruweTekst", tekst))).Should().BeNull();

    [Fact]
    public void KoppelTekst_ZonderBron_GeeftNull_ResolutieStoring()
        => TraceActies.KoppelTekst(Stap("team-herkenning", "", ("ruweTekst", "j10-04"))).Should().BeNull();

    [Theory]
    [InlineData("team-herkenning", "Onopgelost", true)]
    [InlineData("tegenstander-herkenning", "MeerdereKandidaten", true)]
    [InlineData("team-herkenning", "ExacteAlias", false)]
    [InlineData("classificatie", "Onopgelost", false)]
    public void VerwijstNaarWachtrij_BijNietHerkendTeamZonderRuweTekst(string code, string bron, bool verwacht)
        => TraceActies.VerwijstNaarWachtrij(Stap(code, "", ("bron", bron))).Should().Be(verwacht);

    [Fact]
    public void VerwijstNaarWachtrij_NietAlsDeRuweTekstWelBeschikbaarIs_DeTester()
        => TraceActies.VerwijstNaarWachtrij(Stap("team-herkenning", "", ("bron", "Onopgelost"), ("ruweTekst", "j10-04"))).Should().BeFalse();

    [Fact]
    public void ClassificatieType_AlleenBijDeClassificatiestap()
    {
        TraceActies.ClassificatieType(Stap("classificatie", "", ("type", "HerplanVerzoek"))).Should().Be("HerplanVerzoek");
        TraceActies.ClassificatieType(Stap("datum", "", ("type", "HerplanVerzoek"))).Should().BeNull();
        TraceActies.ClassificatieType(Stap("classificatie")).Should().BeNull();
    }

    private static BeslissingsTraceDto Trace(string type, string team, bool zeker, string sjabloon, string leermomenten) => new()
    {
        Oordeel = new TraceOordeelDto { IsZeker = zeker },
        Stappen =
        [
            Stap("leermomenten", "", ("aantal", leermomenten)),
            Stap("classificatie", type, ("type", type)),
            Stap("team-herkenning", team),
            Stap("sjabloon", "", ("sjabloon", "oud-tussenstap")),
            Stap("sjabloon", "", ("sjabloon", sjabloon)),
        ]
    };

    [Fact]
    public void Vergelijking_ToontWatVeranderdeNaEenAlias()
    {
        var voor = Trace("BeschikbaarheidCheck", "Niet herkend", zeker: false, "team-onbekend", "0");
        var na = Trace("BeschikbaarheidCheck", "Herkend als TESTCLUB O10-4", zeker: true, "beschikbaar", "0");

        var rijen = TraceVergelijking.Maak(voor, na);

        rijen.Single(r => r.Label == "Herkend team").Gewijzigd.Should().BeTrue();
        rijen.Single(r => r.Label == "Zekerheid").Voor.Should().Contain("Onzeker");
        rijen.Single(r => r.Label == "Zekerheid").Na.Should().Contain("Zeker (");
        rijen.Single(r => r.Label == "Antwoordsjabloon").Na.Should().Be("beschikbaar", "de laatste sjabloonstap telt (een database-override vervangt de eerste)");
        rijen.Single(r => r.Label == "Verzoektype").Gewijzigd.Should().BeFalse();
    }

    [Fact]
    public void Vergelijking_ToontEenLeermomentAlsGewijzigdAantal()
    {
        var rijen = TraceVergelijking.Maak(
            Trace("BeschikbaarheidCheck", "x", true, "a", "0"), Trace("HerplanVerzoek", "x", true, "a", "1"));

        rijen.Single(r => r.Label == "Leermomenten meegegeven").Na.Should().Be("1");
        rijen.Single(r => r.Label == "Verzoektype").Gewijzigd.Should().BeTrue();
    }

    [Fact]
    public void Vergelijking_ZonderTrace_ToontStrepen_EnFaaltNiet()
    {
        var rijen = TraceVergelijking.Maak(null, null);

        rijen.Should().NotBeEmpty().And.OnlyContain(r => r.Voor == "—" && r.Na == "—" && !r.Gewijzigd);
    }
}
