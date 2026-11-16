using AwesomeAssertions;
using Xunit;

namespace Planner.Shared.Tests;

public sealed class AppSettingsSchrijfveldenTests
{
    [Fact]
    public void BeideTiersDelenEmailVeldenZonderSecretsOfImportmarkers()
    {
        AppSettingsSchrijfvelden.Toegestaan.Should().Contain(new[]
        {
            "EmailVerwerkingIngeschakeld", "EmailReviewModusActief", "EmailReviewOntvanger"
        });
        AppSettingsSchrijfvelden.Toegestaan.Should().NotContain(new[]
        {
            "EmailInstellingenGeimporteerd", "EmailInstellingenBeheerderGewijzigd",
            "BevestigReviewUitzetten", "GraphMailbox", "GraphClientSecret", "SportlinkClientId"
        });
        AppSettingsSchrijfvelden.Toegestaan.Distinct(StringComparer.OrdinalIgnoreCase)
            .Should().HaveCount(AppSettingsSchrijfvelden.Toegestaan.Count);
    }
}
