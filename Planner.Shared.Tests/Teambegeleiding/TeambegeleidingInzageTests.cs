using System.Data;
using AwesomeAssertions;
using Planner.Shared.Teambegeleiding;
using Xunit;

namespace Planner.Shared.Tests.Teambegeleiding;

/// <summary>#1701: het gedeelde contract van het inzagelog voor teambegeleiding.</summary>
public class TeambegeleidingInzageTests
{
    [Fact]
    public void Inzageregel_HeeftAlleenVeldenVoorClubInzienerTeamEnAantal()
    {
        // Structurele garantie: een nieuw veld (bijv. een e-mailadres van de ingeziene begeleider)
        // kan niet ongemerkt in het log belanden — deze test faalt dan en vraagt om een bewuste keuze.
        typeof(TeambegeleidingInzageNieuw).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo("ClubCode", "InzienDoorObjectId", "InzienDoorNaam", "Team", "AantalRijen");
    }

    [Fact]
    public void Retentie_IsTwaalfMaanden()
    {
        TeambegeleidingInzageRetentie.InzageLogMaanden.Should().Be(12);
        TeambegeleidingInzageRetentie.VerwijderVoor(new DateTime(2026, 10, 12, 4, 0, 0, DateTimeKind.Utc))
            .Should().Be(new DateTime(2025, 10, 12, 4, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Maak_KortVrijeWaardenInTotDeKolomlengte_EnLaatKorteWaardenOngemoeid()
    {
        var lang = TeambegeleidingInzageNieuw.Maak("ALLSTARS",
            new TeambegeleidingInzager(new string('o', 80), new string('n', 300)), new string('t', 150), 3);

        lang.InzienDoorObjectId.Should().HaveLength(TeambegeleidingInzageNieuw.MaxObjectId);
        lang.InzienDoorNaam.Should().HaveLength(TeambegeleidingInzageNieuw.MaxNaam);
        lang.Team.Should().HaveLength(TeambegeleidingInzageNieuw.MaxTeam);

        var kort = TeambegeleidingInzageNieuw.Maak("ALLSTARS", new TeambegeleidingInzager(null, null), "JO11-1", 0);
        kort.Should().Be(new TeambegeleidingInzageNieuw("ALLSTARS", null, null, "JO11-1", 0));
    }

    [Fact]
    public void LeesVan_VertaaltNullNaarLeegVoorNaamEnRol_EnNaarNullVoorDeRest()
    {
        var tabel = new DataTable();
        foreach (var kolom in new[] { "naam", "teamrol", "emailadres", "telefoonnummer", "functie" })
            tabel.Columns.Add(kolom, typeof(string));
        tabel.Rows.Add("Jan de Vries", "Trainer", "trainer@voorbeeld.nl", "0000-000001", "Trainer/coach");
        tabel.Rows.Add(DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);

        using var reader = tabel.CreateDataReader();
        reader.Read();
        TeambegeleiderContact.LeesVan(reader).Should().Be(
            new TeambegeleiderContact("Jan de Vries", "Trainer", "trainer@voorbeeld.nl", "0000-000001", "Trainer/coach"));
        reader.Read();
        TeambegeleiderContact.LeesVan(reader).Should().Be(new TeambegeleiderContact("", "", null, null, null));
    }
}
