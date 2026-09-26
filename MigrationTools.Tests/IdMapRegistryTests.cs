using AwesomeAssertions;
using SqlServerToPostgresCopy;
using Xunit;

namespace MigrationTools.Tests;

/// <summary>
/// <see cref="IdMapRegistry"/> houdt bij welke nieuwe Postgres-id hoort bij welke oude
/// SQL-Server-id, zodat een later gekopieerde tabel zijn foreign key kan vertalen (#976).
/// </summary>
/// <remarks>
/// Dit is de enige plek in de kopieerrun waar een verkeerde uitkomst <b>stil</b> is: een fout hier
/// levert geen exception maar een rij die naar het verkeerde bovenliggende record wijst. Vandaar
/// dat juist deze klasse tests krijgt en de omliggende kopieerlus niet — die vraagt twee live
/// databases en faalt luid.
/// </remarks>
public class IdMapRegistryTests
{
    [Fact]
    public void Vertaalt_een_vastgelegde_id()
    {
        var registry = new IdMapRegistry();
        registry.Record("velden", oldId: 7, newId: 41);

        registry.Translate("velden", 7).Should().Be(41);
    }

    [Fact]
    public void Houdt_sleutels_gescheiden()
    {
        var registry = new IdMapRegistry();
        registry.Record("velden", 1, 100);
        registry.Record("teams", 1, 200);

        registry.Translate("velden", 1).Should().Be(100);
        registry.Translate("teams", 1).Should().Be(200);
    }

    [Fact]
    public void Onbekende_sleutel_gooit_met_een_bruikbare_melding()
    {
        var registry = new IdMapRegistry();

        var fout = Assert.Throws<InvalidOperationException>(() => registry.Translate("velden", 7));

        // De melding moet de kopieervolgorde noemen: dat is vrijwel altijd de oorzaak.
        fout.Message.Should().Contain("velden").And.Contain("7").And.Contain("kopieervolgorde");
    }

    [Fact]
    public void Onbekende_id_binnen_een_bekende_sleutel_gooit_ook()
    {
        var registry = new IdMapRegistry();
        registry.Record("velden", 1, 100);

        Assert.Throws<InvalidOperationException>(() => registry.Translate("velden", 2));
    }

    [Fact]
    public void Dezelfde_oude_id_twee_keer_vastleggen_is_een_fout()
    {
        // Stil overschrijven zou betekenen dat de laatst gekopieerde rij wint en eerdere
        // verwijzingen naar de verkeerde rij gaan wijzen — precies de stille fout hierboven.
        var registry = new IdMapRegistry();
        registry.Record("velden", 1, 100);

        Assert.Throws<ArgumentException>(() => registry.Record("velden", 1, 101));
    }
}
