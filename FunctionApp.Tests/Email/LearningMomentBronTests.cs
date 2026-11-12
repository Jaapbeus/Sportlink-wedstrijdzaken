using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

public class LearningMomentBronTests
{
    [FunctionApp.Tests.Feedback.SqlServerFact]
    public async Task BereikbareBronZonderGoedgekeurdeVoorbeelden_GeeftGeldigeLegeSet()
    {
        var connectionString = Environment.GetEnvironmentVariable("SQLSERVER_TEST_CONNECTION_STRING")!;
        var voorbeelden = await LearningMomentRepository.HaalVoorbeeldenOpAsync("ZEMPTY1800", NullLogger.Instance, connectionString);
        voorbeelden.Should().BeEmpty();
    }

    [Theory]
    [InlineData("unsupported=value")]
    [InlineData("Server=127.0.0.1,1;Connect Timeout=1;Encrypt=False;Pooling=False")]
    public async Task OnbeschikbareBron_Propageert_EnIsGeenLegeVoorbeeldset(string connectionString)
    {
        var act = () => LearningMomentRepository.HaalVoorbeeldenOpAsync("ALLSTARS", NullLogger.Instance, connectionString);

        await act.Should().ThrowAsync<Exception>();
    }
}
