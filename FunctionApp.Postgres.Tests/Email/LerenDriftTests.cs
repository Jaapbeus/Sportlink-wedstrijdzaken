using AwesomeAssertions;
using FunctionApp.Postgres.Email;
using Planner.Shared.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>Bewaakt dat de lijst geldige verzoektypen van het leren vanuit de trace (#1568 deel C) gelijk blijft aan de enum van deze tier.</summary>
public class LerenDriftTests
{
    [Fact]
    public void GeldigeVerzoekTypes_BlijftGelijkAanDeEnum()
        => LeermomentInvoer.GeldigeVerzoekTypes.Should().BeEquivalentTo(Enum.GetNames<VerzoekType>());
}
