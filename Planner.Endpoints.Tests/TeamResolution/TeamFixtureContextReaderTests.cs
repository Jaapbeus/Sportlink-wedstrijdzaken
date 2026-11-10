using System.Data;
using Planner.Shared.TeamResolution;
using Planner.Endpoints.TeamResolution;
using Xunit;

namespace Planner.Endpoints.Tests.TeamResolution;

public sealed class TeamFixtureContextReaderTests
{
    [Fact]
    public async Task ReadAsync_FiltersCancelledAndIncompleteRows_PreservesOwnershipAndNullTime()
    {
        var fixtures = Fixtures();
        fixtures.Rows.Add("kept", "MO13-1", "JO13-1", "MO13-1", DBNull.Value, DBNull.Value);
        fixtures.Rows.Add("cancelled", "JO13-1", "JO13-1", "MO13-1", "10:30", " aFgElAsT ");
        for (var column = 0; column < 4; column++)
        {
            object[] row = ["incomplete", "JO13-1", "JO13-1", "MO13-1", "10:30", "Te spelen"];
            row[column] = DBNull.Value;
            fixtures.Rows.Add(row);
        }
        var spellings = Spellings();
        spellings.Rows.Add(1, "JO13-1");
        spellings.Rows.Add(2, "MO13-1");
        await using var reader = new DataTableReader([fixtures, spellings]);
        var context = await TeamFixtureContextReader.ReadAsync(reader);
        Assert.Equal(new TeamFixture("kept", "MO13-1", "JO13-1", "MO13-1", null), Assert.Single(context.Fixtures));
        Assert.Equal(2, context.Spellings.Count);
        Assert.Equal(2, TeamFixtureChoice.Choose(context, 1, 2, null));
        Assert.False(reader.IsClosed);
    }

    [Fact]
    public async Task ReadAsync_EmptyFixtureSet_StillReadsAllAliasesAndDoesNotChoose()
    {
        var spellings = Spellings();
        spellings.Rows.Add(1, "JO13-1");
        spellings.Rows.Add(1, "source spelling");
        await using var reader = new DataTableReader([Fixtures(), spellings]);
        var context = await TeamFixtureContextReader.ReadAsync(reader);
        Assert.Empty(context.Fixtures);
        Assert.Equal(new[] { new TeamFixtureSpelling(1, "JO13-1"), new TeamFixtureSpelling(1, "source spelling") }, context.Spellings);
        Assert.Null(TeamFixtureChoice.Choose(context, 1, 2, null));
    }

    private static DataTable Fixtures()
    {
        var table = new DataTable();
        foreach (var column in new[] { "key", "owner", "home", "away", "time", "status" })
            table.Columns.Add(column, typeof(string));
        return table;
    }

    private static DataTable Spellings()
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("text", typeof(string));
        return table;
    }
}
