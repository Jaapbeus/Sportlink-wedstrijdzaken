using System.Data.Common;
using Planner.Shared.TeamResolution;

namespace Planner.Endpoints.TeamResolution;

/// <summary>Maps the common fixture/spelling result contract; queries and connection ownership remain in each tier.</summary>
public static class TeamFixtureContextReader
{
    public static async Task<TeamFixtureContext> ReadAsync(DbDataReader reader)
    {
        var fixtures = new List<TeamFixture>();
        var spellings = new List<TeamFixtureSpelling>();
        while (await reader.ReadAsync())
        {
            var fixture = TeamFixture.FromSourceRow(Value(0), Value(1), Value(2), Value(3), Value(4), Value(5));
            if (fixture is not null) fixtures.Add(fixture);
        }
        await reader.NextResultAsync();
        while (await reader.ReadAsync())
            spellings.Add(new TeamFixtureSpelling(reader.GetInt32(0), reader.GetString(1)));
        return new TeamFixtureContext(fixtures, spellings);

        string? Value(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
