using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email.Trace;
using Planner.Shared.Leren;
using Xunit;

namespace Planner.Shared.Tests.Email.Trace;

/// <summary>Welke teamtekst belandt in de wachtrij met onbekende teamteksten (#1568 deel C), en dat een storing de verwerking niet breekt.</summary>
public class OnbekendeTeamTekstExtractieTests
{
    private const string Club = "TESTCLUB";

    private static TraceBuilder Builder() => new TraceBuilder().Classificatie("BeschikbaarheidCheck", true, true, 1, true);

    [Fact]
    public void OnopgelosteTeamtekst_KomtInDeWachtrij_MetDezelfdeSleutelAlsDeResolver()
    {
        var trace = Builder()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "j10-04", "Onopgelost", 0, null, null)
            .Bouw();

        var meldingen = OnbekendeTeamTekstExtractie.Uit(trace, Club);

        var melding = meldingen.Should().ContainSingle().Subject;
        melding.Genormaliseerd.Should().Be(TeamNaamNormalisatie.NormaliseerVoorVergelijking("j10-04", Club));
        melding.Voorbeeld.Should().Be("j10-04");
    }

    [Fact]
    public void MeerdereKandidaten_KomtOokInDeWachtrij()
    {
        var trace = Builder()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "13-1", "MeerdereKandidaten", 0, new[] { "A", "B" }, null)
            .Bouw();

        OnbekendeTeamTekstExtractie.Uit(trace, Club).Should().ContainSingle();
    }

    [Fact]
    public void HerkendTeam_KomtNietInDeWachtrij()
    {
        var trace = Builder()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO13-1", "ExacteMatch", 1.0, null, "JO13-1")
            .Bouw();

        OnbekendeTeamTekstExtractie.Uit(trace, Club).Should().BeEmpty();
    }

    [Fact]
    public void TeamtekstDieEenTegenstanderBleek_KomtNietInDeWachtrij()
    {
        // Team onopgelost, maar de "tegenstander" is ons eigen team: de genoemde tekst was gewoon een externe club.
        var trace = Builder()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "FC Elders JO13-1", "Onopgelost", 0, null, null)
            .TeamHerkenning(TraceCodes.TegenstanderHerkenning, "JO13-2", "ExacteMatch", 1.0, null, "JO13-2")
            .Bouw();

        OnbekendeTeamTekstExtractie.Uit(trace, Club).Should().BeEmpty();
    }

    [Fact]
    public void OnopgelosteTegenstander_AlleenIsGeenOnbekendEigenTeam()
    {
        var trace = Builder()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "JO13-1", "ExacteMatch", 1.0, null, "JO13-1")
            .TeamHerkenning(TraceCodes.TegenstanderHerkenning, "FC Elders", "Onopgelost", 0, null, null)
            .Bouw();

        OnbekendeTeamTekstExtractie.Uit(trace, Club).Should().BeEmpty();
    }

    [Fact]
    public void GemaskeerdeWaarde_EnLegeTekst_KomenNietInDeWachtrij()
    {
        var trace = Builder()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "mail trainer@voorbeeld.nl", "Onopgelost", 0, null, null)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "", "Onopgelost", 0, null, null)
            .Bouw();

        OnbekendeTeamTekstExtractie.Uit(trace, Club).Should().BeEmpty();
    }

    [Fact]
    public void ResolutieStoring_ZonderBron_KomtNietInDeWachtrij()
    {
        var trace = Builder().TeamHerkenning(TraceCodes.TeamHerkenning, "j10-04", null, 0, null, null).Bouw();

        OnbekendeTeamTekstExtractie.Uit(trace, Club).Should().BeEmpty();
    }

    [Fact]
    public void ZelfdeSleutelTweemaal_WordtOntdubbeld()
    {
        var trace = Builder()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "j10-04", "Onopgelost", 0, null, null)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "J10-04", "Onopgelost", 0, null, null)
            .Bouw();

        OnbekendeTeamTekstExtractie.Uit(trace, Club).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Pieter en zijn moeder Sanne komen zaterdag niet omdat het regent")]
    [InlineData("Pieter komt zaterdag niet")]
    [InlineData("Sanne")]
    [InlineData("mijn zoon Pieter speelt in 13-2 en 14-1")]
    [InlineData("13-2; DROP TABLE")]
    [InlineData("13-2 <b>")]
    public void VrijeTekstAlsTeam_KomtNietInDeWachtrij(string vrijeTekst)
    {
        var trace = Builder().TeamHerkenning(TraceCodes.TeamHerkenning, vrijeTekst, "Onopgelost", 0, null, null).Bouw();

        OnbekendeTeamTekstExtractie.Uit(trace, Club).Should().BeEmpty();
    }

    [Theory]
    [InlineData("j10-04")]
    [InlineData("JO 13/2")]
    [InlineData("Ajax 13-2")]
    [InlineData("35+1")]
    [InlineData("zin met spatie en veel woorden 3", false)]
    public void StructureelTeamlabel_WordtHerkend(string tekst, bool verwacht = true)
        => OnbekendeTeamTekstExtractie.ZietEruitAlsTeamlabel(tekst).Should().Be(verwacht);

    [Fact]
    public async Task VrijeTekst_KomtNietInDeWachtrijUpsert_MaarEenEchteSchrijfwijzeWel()
    {
        var trace = Builder()
            .TeamHerkenning(TraceCodes.TeamHerkenning, "Pieter komt zaterdag niet vanwege de regen", "Onopgelost", 0, null, null)
            .TeamHerkenning(TraceCodes.TeamHerkenning, "j10-04", "Onopgelost", 0, null, null)
            .Bouw();
        var schrijver = new OpnemendeSchrijver();

        await OnbekendeTeamTekstOpslag.BewaarVeiligAsync(trace, Club, 9, schrijver, NullLogger.Instance);

        var regel = schrijver.Regels.Should().ContainSingle().Subject;
        regel.Item3.Should().Be("j10-04");
        regel.ToString().Should().NotContain("Pieter");
        trace.VoorOpslag().ToJson().Should().NotContain("j10-04").And.NotContain("Pieter");
    }

    [Fact]
    public async Task EenStoringBijHetSchrijven_BreektDeVerwerkingNiet()
    {
        var trace = Builder().TeamHerkenning(TraceCodes.TeamHerkenning, "j10-04", "Onopgelost", 0, null, null).Bouw();
        var schrijver = new FaalSchrijver();

        var act = () => OnbekendeTeamTekstOpslag.BewaarVeiligAsync(trace, Club, 5, schrijver, NullLogger.Instance);

        await act.Should().NotThrowAsync();
        schrijver.Pogingen.Should().Be(1);
    }

    [Fact]
    public async Task Bewaren_GeeftSleutelVoorbeeldEnVerwerkingDoor()
    {
        var trace = Builder().TeamHerkenning(TraceCodes.TeamHerkenning, "j10-04", "Onopgelost", 0, null, null).Bouw();
        var schrijver = new OpnemendeSchrijver();

        await OnbekendeTeamTekstOpslag.BewaarVeiligAsync(trace, Club, 77, schrijver, NullLogger.Instance);

        var regel = schrijver.Regels.Should().ContainSingle().Subject;
        regel.Should().Be((Club, TeamNaamNormalisatie.NormaliseerVoorVergelijking("j10-04", Club), "j10-04", 77));
    }

    private sealed class FaalSchrijver : IOnbekendeTeamTekstSchrijver
    {
        public int Pogingen { get; private set; }
        public Task VoegToeAsync(string clubCode, string genormaliseerd, string voorbeeld, int verwerkingId)
        {
            Pogingen++;
            throw new InvalidOperationException("database weg");
        }
    }

    private sealed class OpnemendeSchrijver : IOnbekendeTeamTekstSchrijver
    {
        public List<(string, string, string, int)> Regels { get; } = new();
        public Task VoegToeAsync(string clubCode, string genormaliseerd, string voorbeeld, int verwerkingId)
        {
            Regels.Add((clubCode, genormaliseerd, voorbeeld, verwerkingId));
            return Task.CompletedTask;
        }
    }
}
