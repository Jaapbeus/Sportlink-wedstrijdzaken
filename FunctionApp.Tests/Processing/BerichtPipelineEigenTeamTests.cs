using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Planner.Shared.TeamResolution;
using SportlinkFunction.Email;
using SportlinkFunction.Processing;
using Xunit;

namespace FunctionApp.Tests.Processing;

/// <summary>
/// #1716 slice 4: het tier-omhulsel <see cref="BerichtPipeline.BepaalEigenTeamEnTegenstanderAsync"/> op de
/// SQL Server-tier. De bepaling zelf staat in <c>Planner.Shared</c> (<c>EigenTeamBepaling</c>) en is daar getest
/// (<c>Planner.Shared.Tests/TeamResolution/EigenTeamBepalingTests.cs</c>). Hier alleen: het omhulsel zet
/// de uitkomst terug op de classificatie, en laat die ongewijzigd als er niets herkend is. Geen database
/// nodig. Tegenhanger: <c>FunctionApp.Postgres.Tests/Processing/BerichtPipelineEigenTeamTests.cs</c>.
/// </summary>
public class BerichtPipelineEigenTeamTests
{
    private const string Club = "TESTCLUB";

    [Fact]
    public async Task Omhulsel_TeamInTegenstanderveld_ZetBeideVeldenOpDeClassificatie()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck, TeamNaam = "Tegenstander JO13-1", Tegenstander = "JO13-2" };

        var herkend = await BerichtPipeline.BepaalEigenTeamEnTegenstanderAsync(
            classificatie, new VasteResolver("JO13-2"), Club, NullLogger.Instance, null);

        herkend.Should().BeTrue();
        classificatie.TeamNaam.Should().Be("TESTCLUB JO13-2");
        classificatie.Tegenstander.Should().Be("Tegenstander JO13-1");
    }

    [Fact]
    public async Task Omhulsel_NietsHerkend_LaatClassificatieOngewijzigd()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck, TeamNaam = " 13-1 ", Tegenstander = " Onbekend " };

        var herkend = await BerichtPipeline.BepaalEigenTeamEnTegenstanderAsync(
            classificatie, new VasteResolver("JO13-2"), Club, NullLogger.Instance, null);

        herkend.Should().BeFalse();
        classificatie.TeamNaam.Should().Be(" 13-1 ");
        classificatie.Tegenstander.Should().Be(" Onbekend ");
    }

    /// <summary>Lost alleen de opgegeven tekst op, naar "TESTCLUB " + tekst.</summary>
    private sealed class VasteResolver(string bekend) : ITeamResolver
    {
        public Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
            => Task.FromResult(request.RuweTeamTekst == bekend
                ? new TeamResolutionResult(7, "TESTCLUB " + bekend, 1.0, [], ResolutionBron.ExacteMatch)
                : TeamResolutionResult.Onopgelost);
    }
}
