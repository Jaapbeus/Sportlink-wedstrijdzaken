using FluentAssertions;
using FunctionApp.Postgres.Admin;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #952: de testdata-beheerpagina genereert client-side een tekstsleutel ("ALLSTARS-&lt;hex&gt;"),
/// maar <c>his.matches.bk_matches</c> is op deze tier een <c>GENERATED ALWAYS</c>-kolom afgeleid van
/// het numerieke <c>wedstrijdcode</c>. <see cref="AdminTestDataFunction.DeriveWedstrijdcode"/> maakt
/// die vertaling — deze tests dekken de eigenschappen waar de upsert/delete-cyclus op leunt.
/// </summary>
public class AdminTestDataFunctionTests
{
    [Fact]
    public void DeriveWedstrijdcode_ZelfdeTekstsleutel_GeeftAltijdDezelfdeCode()
    {
        var eerste = AdminTestDataFunction.DeriveWedstrijdcode("ALLSTARS-3f9a2b1c4d5e");
        var tweede = AdminTestDataFunction.DeriveWedstrijdcode("ALLSTARS-3f9a2b1c4d5e");

        eerste.Should().Be(tweede);
    }

    [Fact]
    public void DeriveWedstrijdcode_VerschillendeTekstsleutels_GevenVerschillendeCodes()
    {
        var a = AdminTestDataFunction.DeriveWedstrijdcode("ALLSTARS-aaaaaaaaaaaa");
        var b = AdminTestDataFunction.DeriveWedstrijdcode("ALLSTARS-bbbbbbbbbbbb");

        a.Should().NotBe(b);
    }

    [Fact]
    public void DeriveWedstrijdcode_TekstsleutelValtBuitenGezaaidDemobereikEnEchteWedstrijdcodes()
    {
        var code = AdminTestDataFunction.DeriveWedstrijdcode("ALLSTARS-3f9a2b1c4d5e");

        // Gezaaid demobereik (003-seed-allstars-demo-matches-postgres.sql): 9.000.001-9.000.224.
        // Echte Sportlink-wedstrijdcodes (FunctionApp/CLAUDE.md-voorbeeld): 8 cijfers, dus < 100.000.000.
        code.Should().BeGreaterThanOrEqualTo(900_000_000L);
        code.Should().BeLessThan(990_000_000L);
    }

    [Fact]
    public void DeriveWedstrijdcode_AlNumeriekeSleutel_WordtRechtstreeksGebruikt()
    {
        // Scenario ná een paginaherlaad: de pagina heeft de door de database afgeleide bk_matches
        // (== wedstrijdcode als tekst) teruggekregen en stuurt die ongewijzigd terug bij een update.
        var code = AdminTestDataFunction.DeriveWedstrijdcode("900012345");

        code.Should().Be(900012345L);
    }
}
