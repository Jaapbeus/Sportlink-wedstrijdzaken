using FluentAssertions;
using FunctionApp.Postgres;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #1098 (tweede v3.3.0.0-incident): release v3.3.0.0 selecteerde <c>sportlinkextensionenabled</c>
/// (migratie 012) terwijl die migratie in productie nog niet was toegepast. Postgres antwoordde
/// <c>42703 undefined_column</c>, <c>WaitForDatabaseAsync</c> zag "database onbereikbaar" en elk
/// beheerscherm eindigde na vijf herhalingen in 500 "Ophalen mislukt".
/// <para>
/// De fallback in <see cref="PostgresAppSettings.LoadSettingsAsync"/> mag uitsluitend op díe ene
/// kolom aanslaan. Deze tests toetsen de herkenning zelf — het databasepad is end-to-end
/// geverifieerd tegen een lokale database waar de kolom bewust verwijderd was (zie het issue).
/// </para>
/// </summary>
public class PostgresAppSettingsSchemaFallbackTests
{
    private static PostgresException Undefined(string sqlState, string messageText)
        => new(messageText, "ERROR", "ERROR", sqlState);

    [Fact]
    public void IsOntbrekendeKolom_Herkent42703VoorDeExtensiekolom()
    {
        var ex = Undefined(PostgresErrorCodes.UndefinedColumn, "column \"sportlinkextensionenabled\" does not exist");

        PostgresAppSettings.IsOntbrekendeKolom(ex, PostgresAppSettings.ExtensionColumn).Should().BeTrue();
    }

    [Fact]
    public void IsOntbrekendeKolom_AndereOntbrekendeKolom_BlijftEenHardeFout()
    {
        var ex = Undefined(PostgresErrorCodes.UndefinedColumn, "column \"clubname\" does not exist");

        PostgresAppSettings.IsOntbrekendeKolom(ex, PostgresAppSettings.ExtensionColumn).Should().BeFalse();
    }

    [Fact]
    public void IsOntbrekendeKolom_AndereSqlState_BlijftEenHardeFout()
    {
        // 42P01 undefined_table met de kolomnaam in de tekst — verkeerde foutklasse, geen fallback.
        var ex = Undefined(PostgresErrorCodes.UndefinedTable, "relation \"sportlinkextensionenabled\" does not exist");

        PostgresAppSettings.IsOntbrekendeKolom(ex, PostgresAppSettings.ExtensionColumn).Should().BeFalse();
    }

    [Fact]
    public void ResetForTests_WistDeSchemawaarschuwing()
    {
        PostgresAppSettings.ResetForTests();

        PostgresAppSettings.SchemaWarning.Should().BeNull();
        PostgresAppSettings.LastLoadFailed.Should().BeFalse();
    }
}
