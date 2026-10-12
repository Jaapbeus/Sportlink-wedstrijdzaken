using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>Logger die de geformatteerde regels opvangt, om te bewijzen dát er gelogd wordt en dat er geen inhoud in staat.</summary>
internal sealed class OpvangendeLogger : ILogger
{
    public List<string> Regels { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Regels.Add(formatter(state, exception));
}

/// <summary>
/// Naamvorm van team en tegenstander (#1673): gewone Nederlandse clubnamen (apostrof vooraan, haakjes,
/// komma, puntafkortingen) komen door; URL's, markup, regeleinden en placeholdertekens niet.
/// </summary>
public class AiVeldNaamvormTests
{
    [Theory]
    [InlineData("Voorbeeld")]
    [InlineData("JO11-1")]
    [InlineData("FC Voorbeeld")]
    [InlineData("'t Voorbeeld")]
    [InlineData("’t Voorbeeld")]
    [InlineData("'s-Voorbeeld SV")]
    [InlineData("SV Voorbeeld (Noord)")]
    [InlineData("FC Voorbeeld, JO11-3")]
    [InlineData("v.v.Voorbeeld")]
    [InlineData("V.V.Voorbeeld JO13-1")]
    [InlineData("v.v. Voorbeeld")]
    [InlineData("S.V. Voorbeeld")]
    [InlineData("Voorbeeld & Co")]
    public void TegenstanderWeergave_RealistischeNaamKomtDoor(string naam)
        => AiVeldSanering.TegenstanderWeergave(naam).Should().Be(naam);

    [Theory]
    [InlineData("'t Voorbeeld")]
    [InlineData("SV Voorbeeld (Noord)")]
    [InlineData("v.v.Voorbeeld")]
    public void TeamNaamWeergave_RealistischeBevestigdeNaamKomtDoor(string naam)
        => AiVeldSanering.TeamNaamWeergave(naam, onbevestigd: false).Should().Be(naam);

    [Theory]
    [InlineData("voorbeeld.nl")]
    [InlineData("Voorbeeld.NL")]
    [InlineData("sub.voorbeeld.test")]
    [InlineData("t.co")]
    [InlineData("Zie t.co/abc")]
    [InlineData("FC Voorbeeld https://x")]
    [InlineData("www Voorbeeld")]
    [InlineData("<b>Voorbeeld</b>")]
    [InlineData("{{team}}")]
    [InlineData("[Voorbeeld]")]
    [InlineData("Voorbeeld\nTweede regel")]
    [InlineData("Voorbeeld `code`")]
    [InlineData("(Voorbeeld)")]
    [InlineData("''Voorbeeld")]
    [InlineData("trainer@voorbeeld.nl")]
    public void TegenstanderWeergave_UrlMarkupOfPlaceholderVervalt(string invoer)
        => AiVeldSanering.TegenstanderWeergave(invoer).Should().BeNull();
}

/// <summary>Generieke tekst voor sjabloonplaatshouders als de naam niet getoond mag worden (#1673).</summary>
public class AiVeldTeamNaamOfGeneriekTests
{
    [Fact]
    public void BevestigdeNaamWordtGenoemd()
        => AiVeldSanering.TeamNaamOfGeneriek("JO13-2", onbevestigd: false).Should().Be("JO13-2");

    [Fact]
    public void OnbevestigdeNaamKrijgtGeneriekeTekst()
        => AiVeldSanering.TeamNaamOfGeneriek("JO13-2", onbevestigd: true).Should().Be(AiVeldSanering.GeneriekTeam);

    [Fact]
    public void OngeldigeNaamKrijgtGeneriekeTekst()
        => AiVeldSanering.TeamNaamOfGeneriek("JO13 https://voorbeeld.test", onbevestigd: false).Should().Be("het opgegeven team");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ZonderNaamBlijftHetLeeg(string? invoer)
        => AiVeldSanering.TeamNaamOfGeneriek(invoer, onbevestigd: true).Should().BeEmpty();

    [Fact]
    public void TegenstanderOfGeneriek_StandaardGeneriekeTekst()
        => AiVeldSanering.TegenstanderOfGeneriek("Ajax www.voorbeeld.test").Should().Be("de opgegeven tegenstander");
}

/// <summary>
/// Een vervallen modelveld en een ingekorte berichttekst laten een logregel achter, zonder inhoud van
/// het bericht (#1673, #1674).
/// </summary>
public class AiVeldVervalLogTests
{
    [Fact]
    public void KnvbNotitie_VervallenWaardeWordtGelogdZonderInhoud()
    {
        var log = new OpvangendeLogger();

        AiVeldSanering.KnvbNotitie("Zie https://voorbeeld.test/geheim", log).Should().BeNull();

        log.Regels.Should().ContainSingle().Which.Should().Contain("knvbNotitie").And.NotContain("voorbeeld.test");
    }

    [Fact]
    public void AanvangsTijd_VervallenWaardeWordtGelogdZonderInhoud()
    {
        var log = new OpvangendeLogger();

        AiVeldSanering.AanvangsTijd("half drie", log).Should().BeNull();

        log.Regels.Should().ContainSingle().Which.Should().Contain("aanvangsTijd").And.NotContain("half drie");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void LegeWaardeIsGeenVerval(string? invoer)
    {
        var log = new OpvangendeLogger();

        AiVeldSanering.KnvbNotitie(invoer, log).Should().BeNull();
        AiVeldSanering.AanvangsTijd(invoer, log).Should().BeNull();

        log.Regels.Should().BeEmpty();
    }

    [Fact]
    public void GeldigeWaardeWordtNietGelogd()
    {
        var log = new OpvangendeLogger();

        AiVeldSanering.AanvangsTijd("14:30", log).Should().Be("14:30");
        AiVeldSanering.KnvbNotitie("Korte notitie", log).Should().Be("Korte notitie");

        log.Regels.Should().BeEmpty();
    }

    [Fact]
    public void BodyKap_AfkappenWordtGelogdMetAlleenLengtes()
    {
        var log = new OpvangendeLogger();
        var body = "geheime inhoud " + new string('x', BerichtBodyBegrenzing.MaxBodyTekens);

        BerichtBodyBegrenzing.Kap(body, log).Should().EndWith(BerichtBodyBegrenzing.AfkapMarkering);

        log.Regels.Should().ContainSingle().Which.Should().Contain(body.Length.ToString()).And.NotContain("geheime");
    }

    [Fact]
    public void BodyKap_KorteBodyWordtNietGelogd()
    {
        var log = new OpvangendeLogger();

        BerichtBodyBegrenzing.Kap("kort", log).Should().Be("kort");

        log.Regels.Should().BeEmpty();
    }
}
