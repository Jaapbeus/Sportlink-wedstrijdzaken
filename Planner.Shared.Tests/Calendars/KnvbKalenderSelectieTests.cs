using AwesomeAssertions;
using Planner.Shared.Calendars;
using Xunit;

namespace Planner.Shared.Tests.Calendars;

public class KnvbKalenderSelectieTests
{
    private static readonly DateOnly Zaterdag1 = new(2026, 9, 5);
    private static readonly DateOnly Zaterdag2 = new(2026, 9, 12);
    private static readonly DateOnly Vrijdag = new(2026, 9, 4);

    [Fact]
    public void SelecteertAlleenDagtypesMetWedstrijden()
    {
        var dagen = new[]
        {
            new KnvbKalenderDag(Zaterdag1, "Competitie"),
            new KnvbKalenderDag(Zaterdag2, "Beker"),
            new KnvbKalenderDag(new DateOnly(2026, 9, 19), "Inhaal"),
            new KnvbKalenderDag(new DateOnly(2026, 9, 26), "Vrij"),
        };

        KnvbKalenderSelectie.SelecteerVrijeZaterdagen(
            dagen, Zaterdag1, new DateOnly(2026, 9, 26), new HashSet<DateOnly>(), 10)
            .Should().Equal(Zaterdag1, Zaterdag2, new DateOnly(2026, 9, 19));
    }

    [Fact]
    public void SelecteertDatumgrenzenInclusief()
    {
        var dagen = new[]
        {
            new KnvbKalenderDag(new DateOnly(2026, 8, 29), "Competitie"),
            new KnvbKalenderDag(Zaterdag1, "Competitie"),
            new KnvbKalenderDag(Zaterdag2, "Competitie"),
            new KnvbKalenderDag(new DateOnly(2026, 9, 19), "Competitie"),
        };

        KnvbKalenderSelectie.SelecteerVrijeZaterdagen(
            dagen, Zaterdag1, Zaterdag2, new HashSet<DateOnly>(), 10)
            .Should().Equal(Zaterdag1, Zaterdag2);
    }

    [Fact]
    public void SluitNietZaterdagenUit()
    {
        var dagen = new[]
        {
            new KnvbKalenderDag(Vrijdag, "Competitie"),
            new KnvbKalenderDag(Zaterdag1, "Competitie"),
        };

        KnvbKalenderSelectie.SelecteerVrijeZaterdagen(
            dagen, Vrijdag, Zaterdag1, new HashSet<DateOnly>(), 10)
            .Should().Equal(Zaterdag1);
    }

    [Fact]
    public void SluitReedsBezetteDataUit()
    {
        var dagen = new[]
        {
            new KnvbKalenderDag(Zaterdag1, "Competitie"),
            new KnvbKalenderDag(Zaterdag2, "Beker"),
        };

        KnvbKalenderSelectie.SelecteerVrijeZaterdagen(
            dagen, Zaterdag1, Zaterdag2, new HashSet<DateOnly> { Zaterdag1 }, 10)
            .Should().Equal(Zaterdag2);
    }

    [Fact]
    public void SorteertOplopendEnBegrenstAantal()
    {
        var dagen = new[]
        {
            new KnvbKalenderDag(Zaterdag2, "Inhaal"),
            new KnvbKalenderDag(Zaterdag1, "Competitie"),
        };

        KnvbKalenderSelectie.SelecteerVrijeZaterdagen(
            dagen, Zaterdag1, Zaterdag2, new HashSet<DateOnly>(), 1)
            .Should().Equal(Zaterdag1);
    }

    [Fact]
    public void MaxAantalNietPositiefOfOmgekeerdBereikGeeftLegeLijst()
    {
        var dagen = new[] { new KnvbKalenderDag(Zaterdag1, "Competitie") };

        KnvbKalenderSelectie.SelecteerVrijeZaterdagen(
            dagen, Zaterdag1, Zaterdag2, new HashSet<DateOnly>(), 0).Should().BeEmpty();
        KnvbKalenderSelectie.SelecteerVrijeZaterdagen(
            dagen, Zaterdag2, Zaterdag1, new HashSet<DateOnly>(), 1).Should().BeEmpty();
    }
}
