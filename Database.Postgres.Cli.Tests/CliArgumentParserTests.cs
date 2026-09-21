using AwesomeAssertions;
using Database.Postgres.Cli;
using Xunit;

namespace Database.Postgres.Cli.Tests;

/// <summary>
/// De argumentafhandeling van de migratie-CLI (#1302).
/// </summary>
/// <remarks>
/// Deze keuze bepaalt wat <c>deploy.yml</c> doet: migraties toepassen, <c>his</c>-tabellen
/// aanmaken, of demodata seeden. Een verkeerde uitkomst is een verkeerde deploy, en dat merk je
/// pas aan de productiedatabase.
/// </remarks>
public class CliArgumentParserTests
{
    [Fact]
    public void Zonder_argumenten_draait_de_migratiestand()
    {
        var cli = CliArgumentParser.Lees([]);

        cli.EnsureHisTables.Should().BeFalse();
        cli.SeedDemodata.Should().BeFalse();
        cli.SeedScriptPad.Should().BeNull();
        cli.MigratiePad.Should().BeNull("dan valt de CLI terug op de migratiemap in de repository");
    }

    [Fact]
    public void Een_positioneel_argument_is_de_migratiemap()
    {
        CliArgumentParser.Lees(["/tmp/basismigraties"]).MigratiePad.Should().Be("/tmp/basismigraties");
    }

    [Fact]
    public void Ensure_his_tables_wordt_herkend()
    {
        var cli = CliArgumentParser.Lees(["--ensure-his-tables"]);

        cli.EnsureHisTables.Should().BeTrue();
        cli.SeedDemodata.Should().BeFalse();
    }

    [Fact]
    public void Seed_demodata_zonder_pad_valt_terug_op_het_script_in_de_repository()
    {
        var cli = CliArgumentParser.Lees(["--seed-demodata"]);

        cli.SeedDemodata.Should().BeTrue();
        cli.SeedScriptPad.Should().BeNull();
    }

    [Fact]
    public void Seed_demodata_neemt_het_pad_erachter_over()
    {
        var cli = CliArgumentParser.Lees(["--seed-demodata", "/tmp/seed.sql"]);

        cli.SeedDemodata.Should().BeTrue();
        cli.SeedScriptPad.Should().Be("/tmp/seed.sql");
    }

    [Fact]
    public void Een_vlag_achter_seed_demodata_is_geen_bestandsnaam()
    {
        // Zonder deze regel zou '--seed-demodata --ensure-his-tables' proberen een bestand met de
        // naam "--ensure-his-tables" te openen.
        var cli = CliArgumentParser.Lees(["--seed-demodata", "--ensure-his-tables"]);

        cli.SeedDemodata.Should().BeTrue();
        cli.SeedScriptPad.Should().BeNull();
        cli.EnsureHisTables.Should().BeTrue();
    }

    [Fact]
    public void Het_seedscriptpad_wordt_niet_ook_als_migratiemap_gelezen()
    {
        var cli = CliArgumentParser.Lees(["--seed-demodata", "/tmp/seed.sql", "/tmp/migraties"]);

        cli.SeedScriptPad.Should().Be("/tmp/seed.sql");
        cli.MigratiePad.Should().Be("/tmp/migraties");
    }

    [Fact]
    public void Vlaggen_mogen_in_elke_volgorde_staan()
    {
        var cli = CliArgumentParser.Lees(["--ensure-his-tables", "/tmp/migraties", "--seed-demodata"]);

        cli.EnsureHisTables.Should().BeTrue();
        cli.SeedDemodata.Should().BeTrue();
        cli.MigratiePad.Should().Be("/tmp/migraties");
    }

    [Fact]
    public void Onbekende_vlaggen_worden_genegeerd_en_niet_als_pad_gelezen()
    {
        var cli = CliArgumentParser.Lees(["--verbose", "/tmp/migraties"]);

        cli.MigratiePad.Should().Be("/tmp/migraties");
    }

    [Fact]
    public void Alleen_het_eerste_positionele_argument_telt_als_migratiemap()
    {
        CliArgumentParser.Lees(["/tmp/een", "/tmp/twee"]).MigratiePad.Should().Be("/tmp/een");
    }

    [Fact]
    public void Null_als_argumentenlijst_is_een_programmeerfout()
        => Assert.Throws<ArgumentNullException>(() => CliArgumentParser.Lees(null!));
}
