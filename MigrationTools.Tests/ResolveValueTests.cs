using AwesomeAssertions;
using SqlServerToPostgresCopy;
using Xunit;

namespace MigrationTools.Tests;

/// <summary>
/// <see cref="TableCopier.ResolveValue"/> beslist per kolom of de bronwaarde ongewijzigd mee mag,
/// of dat hij eerst door <see cref="IdMapRegistry"/> vertaald moet worden (#976).
/// </summary>
public class ResolveValueTests
{
    private static TableMapping Mapping(IReadOnlyDictionary<string, string>? remaps = null)
        => new("dbo", "Bron", "public", "doel", HasClubCode: true, IdentityMapKey: null, ForeignKeyRemaps: remaps);

    [Fact]
    public void Null_blijft_null_ook_als_de_kolom_geremapt_wordt()
    {
        // Een NULL foreign key mag nooit een verzonnen id worden; dat zou een rij aan een
        // willekeurig bovenliggend record koppelen in plaats van hem los te laten.
        var mapping = Mapping(new Dictionary<string, string> { ["veldid"] = "velden" });

        var resultaat = TableCopier.ResolveValue(mapping, "veldid", DBNull.Value, new IdMapRegistry());

        resultaat.Should().Be(DBNull.Value);
    }

    [Fact]
    public void Kolom_zonder_remap_gaat_ongewijzigd_door()
    {
        var resultaat = TableCopier.ResolveValue(Mapping(), "naam", "Veld 1", new IdMapRegistry());

        resultaat.Should().Be("Veld 1");
    }

    [Fact]
    public void Kolom_die_niet_in_de_remaplijst_staat_gaat_ongewijzigd_door()
    {
        var mapping = Mapping(new Dictionary<string, string> { ["veldid"] = "velden" });

        var resultaat = TableCopier.ResolveValue(mapping, "naam", "Veld 1", new IdMapRegistry());

        resultaat.Should().Be("Veld 1");
    }

    [Fact]
    public void Geremapte_kolom_krijgt_de_nieuwe_id()
    {
        var registry = new IdMapRegistry();
        registry.Record("velden", oldId: 7, newId: 41);
        var mapping = Mapping(new Dictionary<string, string> { ["veldid"] = "velden" });

        var resultaat = TableCopier.ResolveValue(mapping, "veldid", 7, registry);

        resultaat.Should().Be(41L);
    }

    [Fact]
    public void Een_int_bronwaarde_wordt_als_long_vertaald()
    {
        // SQL Server levert een INT-identity als int; de registry werkt met long. Zonder de
        // Convert.ToInt64 zou de lookup hier mislukken op het type in plaats van op de waarde.
        var registry = new IdMapRegistry();
        registry.Record("velden", 7, 41);
        var mapping = Mapping(new Dictionary<string, string> { ["veldid"] = "velden" });

        TableCopier.ResolveValue(mapping, "veldid", (int)7, registry).Should().Be(41L);
    }

    [Fact]
    public void Een_onbekende_id_in_een_geremapte_kolom_faalt_luid()
    {
        var mapping = Mapping(new Dictionary<string, string> { ["veldid"] = "velden" });

        Assert.Throws<InvalidOperationException>(
            () => TableCopier.ResolveValue(mapping, "veldid", 7, new IdMapRegistry()));
    }
}
