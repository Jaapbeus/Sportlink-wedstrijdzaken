using Microsoft.AspNetCore.Http;
using Planner.Endpoints.Admin;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

public sealed class EmailLogEndpointCoreTests
{
    [Fact]
    public void LeesFilter_DateOnly_PreservesUtcCalendarDaySemantics()
    {
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["vanaf"] = "2026-11-09",
            ["tot"] = "2026-11-09"
        });
        var filter = EmailLogEndpointCore.LeesFilter(query);
        Assert.Equal(new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), filter.Vanaf);
        Assert.Equal(new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc), filter.Tot);
    }

    [Fact]
    public void LeesFilter_Timestamp_PreservesExactUtcBoundary()
    {
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["vanaf"] = "2026-11-09T15:04:05+01:00"
        });
        var filter = EmailLogEndpointCore.LeesFilter(query);
        Assert.Equal(new DateTime(2026, 11, 9, 14, 4, 5, DateTimeKind.Utc), filter.Vanaf);
    }

    [Theory]
    [InlineData("2026-11-09T00:00:00Z", 9, 0, 0)]
    [InlineData("2026-11-09 15:04", 9, 15, 4)]
    public void LeesFilter_TotMetTijd_IsExacteExclusieveGrens(string waarde, int dag, int uur, int minuut)
    {
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["tot"] = waarde
        });
        var filter = EmailLogEndpointCore.LeesFilter(query);
        Assert.Equal(new DateTime(2026, 11, dag, uur, minuut, 0, DateTimeKind.Utc), filter.Tot);
    }
}
