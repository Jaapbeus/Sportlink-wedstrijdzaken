using AwesomeAssertions;
using Planner.Shared.Autorisatie;
using Xunit;

namespace Planner.Shared.Tests.Autorisatie;

public class PlannerAanvragerIdentiteitTests
{
    private const string ObjectId = "8e4234ee-4b03-4c01-9726-e02f41a0be62";

    [Fact]
    public void BepaalOpgeslagenLabel_GeldigeNaam_BewaartAlleenWeergavenaam()
        => PlannerAanvragerIdentiteit.BepaalOpgeslagenLabel(ObjectId, " Jan de Vries ", false)
            .Should().Be("Jan de Vries");

    [Fact]
    public void BepaalOpgeslagenLabel_OngeldigeNaam_ValtTerugOpCanoniekObjectId()
    {
        var ongeldigeNamen = new[]
        {
            "trainer@voorbeeld.nl",
            "Jan de Vries\n",
            new string('x', 201),
            new string('é', 101)
        };

        foreach (var naam in ongeldigeNamen)
            PlannerAanvragerIdentiteit.BepaalOpgeslagenLabel(ObjectId.ToUpperInvariant(), naam, false)
                .Should().Be(ObjectId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-object-id")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void BepaalOpgeslagenLabel_OntbrekendOfOngeldigObjectId_Weigert(string? objectId)
        => PlannerAanvragerIdentiteit.BepaalOpgeslagenLabel(objectId, "Jan de Vries", false)
            .Should().BeNull();

    [Fact]
    public void BepaalOpgeslagenLabel_LokaleBypass_GeeftAlleenSynthetischeIdentiteit()
        => PlannerAanvragerIdentiteit.BepaalOpgeslagenLabel(null, null, true)
            .Should().Be("lokale-ontwikkelaar");

    [Fact]
    public void ProbeerToepassen_OntbrekendObjectId_WeigertZonderCallback()
    {
        var callbackCalled = false;

        var applied = PlannerAanvragerIdentiteit.ProbeerToepassen(
            null, "Jan de Vries", false, _ => callbackCalled = true);

        applied.Should().BeFalse();
        callbackCalled.Should().BeFalse();
    }
}
