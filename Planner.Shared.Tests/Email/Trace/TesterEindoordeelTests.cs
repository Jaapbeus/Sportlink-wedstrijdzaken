using AwesomeAssertions;
using Planner.Shared.Email.Trace;
using Xunit;

namespace Planner.Shared.Tests.Email.Trace;

/// <summary>#1583: de e-mailtester toont een definitief eindoordeel dat de zekerheidspoort-instelling meeweegt.</summary>
public class TesterEindoordeelTests
{
    [Fact]
    public void Onzeker_PoortAan_GaatNaarReview_EnHetAntwoordIsEenConcept()
    {
        var e = TesterEindoordeel.Bepaal(true, null, poortActief: true, isZeker: false);

        e.Uitkomst.Should().Be(TesterUitkomst.Review);
        e.Titel.Should().Be("Gaat naar Review — er wordt géén antwoord verstuurd");
        e.Waarschuwing.Should().BeFalse();
        e.ConceptLabel.Should().Contain("Concept").And.Contain("review");
    }

    [Fact]
    public void Zeker_PoortAan_WordtAutomatischVerstuurd()
    {
        var e = TesterEindoordeel.Bepaal(true, null, poortActief: true, isZeker: true);

        e.Uitkomst.Should().Be(TesterUitkomst.AutomatischVerstuurd);
        e.Titel.Should().Be("Wordt automatisch verstuurd");
        e.Waarschuwing.Should().BeFalse();
        e.ConceptLabel.Should().Contain("zou worden verstuurd");
    }

    [Fact]
    public void Zeker_PoortUit_WordtAutomatischVerstuurd_ZonderWaarschuwing()
        => TesterEindoordeel.Bepaal(true, null, poortActief: false, isZeker: true)
            .Should().Match<TesterEindoordeel>(e => e.Uitkomst == TesterUitkomst.AutomatischVerstuurd && !e.Waarschuwing);

    [Fact]
    public void Onzeker_PoortUit_WordtToch_AutomatischVerstuurd_MetWaarschuwing()
    {
        var e = TesterEindoordeel.Bepaal(true, null, poortActief: false, isZeker: false);

        e.Uitkomst.Should().Be(TesterUitkomst.AutomatischVerstuurd);
        e.Waarschuwing.Should().BeTrue();
        e.Titel.Should().Contain("onzeker");
        e.Toelichting.Should().Contain("zekerheidspoort staat uit");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ReplyBeleidDatZwijgt_GaatVoorDePoort(bool poortActief, bool isZeker)
    {
        var e = TesterEindoordeel.Bepaal(false, "Planning mogelijk op de gevraagde datum", poortActief, isZeker);

        e.Uitkomst.Should().Be(TesterUitkomst.GeenAntwoord);
        e.Toelichting.Should().Contain("Planning mogelijk op de gevraagde datum");
        e.Waarschuwing.Should().BeFalse();
    }

    [Fact]
    public void ReplyBeleidDatZwijgt_ZonderReden_HeeftToch_EenToelichting()
        => TesterEindoordeel.Bepaal(false, " ", true, true).Toelichting.Should().NotBeNullOrWhiteSpace();
}
