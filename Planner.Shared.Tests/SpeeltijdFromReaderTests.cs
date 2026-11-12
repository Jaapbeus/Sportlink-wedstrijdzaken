using System.Data;
using AwesomeAssertions;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// #1911: <c>Speeltijd.FromReader</c> casten een <c>time</c>-kolom hard naar <see cref="TimeSpan"/>. Npgsql 10 levert
/// via <c>GetValue</c> een <see cref="TimeOnly"/>, SqlClient een <see cref="TimeSpan"/>; de Planning gaf daardoor
/// "Interne fout" op productie. Deze tests dekken beide driververschijningsvormen zonder database.
/// </summary>
public class SpeeltijdFromReaderTests
{
    private static DataTableReader Reader(Type tijdType, object? tijd)
    {
        var tabel = new DataTable();
        tabel.Columns.Add("leeftijd", typeof(string));
        tabel.Columns.Add("veldafmeting", typeof(decimal));
        tabel.Columns.Add("wedstrijdtotaal", typeof(int));
        tabel.Columns.Add("standaardvoorkeurtijd", tijdType);
        tabel.Rows.Add("JO9", 0.5m, 60, tijd ?? DBNull.Value);
        var reader = tabel.CreateDataReader();
        reader.Read();
        return reader;
    }

    [Fact]
    public void FromReader_TimeOnlyKolom_Npgsql10_WerktZonderCastFout()
    {
        using var reader = Reader(typeof(TimeOnly), new TimeOnly(10, 30));
        Speeltijd.FromReader(reader).StandaardVoorkeurTijd.Should().Be(new TimeOnly(10, 30));
    }

    [Fact]
    public void FromReader_TimeSpanKolom_SqlServer_WerktOngewijzigd()
    {
        using var reader = Reader(typeof(TimeSpan), new TimeSpan(9, 0, 0));
        Speeltijd.FromReader(reader).StandaardVoorkeurTijd.Should().Be(new TimeOnly(9, 0));
    }

    [Theory]
    [InlineData(typeof(TimeOnly))]
    [InlineData(typeof(TimeSpan))]
    public void FromReader_NullTijd_GeeftNull(Type tijdType)
    {
        using var reader = Reader(tijdType, null);
        var speeltijd = Speeltijd.FromReader(reader);
        speeltijd.StandaardVoorkeurTijd.Should().BeNull();
        speeltijd.Leeftijd.Should().Be("JO9");
    }

    [Fact]
    public void TijdVanKolom_OnbekendType_GeeftDuidelijkeFout()
    {
        var act = () => Speeltijd.TijdVanKolom("10:30");
        act.Should().Throw<InvalidCastException>().WithMessage("*String*");
    }
}
