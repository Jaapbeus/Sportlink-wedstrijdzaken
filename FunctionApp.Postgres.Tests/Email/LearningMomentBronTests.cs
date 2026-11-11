using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using FunctionApp.Postgres.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

public class LearningMomentBronTests
{
    [Database.Postgres.Tests.PostgresFact]
    public async Task BereikbareBronZonderGoedgekeurdeVoorbeelden_GeeftGeldigeLegeSet()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION_STRING")!;
        var voorbeelden = await LearningMomentRepository.HaalVoorbeeldenOpAsync(connectionString, "ZEMPTY1800", NullLogger.Instance);
        voorbeelden.Should().BeEmpty();
    }

    [Theory]
    [InlineData("unsupported=value")]
    [InlineData("Host=127.0.0.1;Port=1;Username=allstars;Timeout=1;Pooling=false")]
    public async Task OnbeschikbareBron_Propageert_EnIsGeenLegeVoorbeeldset(string connectionString)
    {
        var act = () => LearningMomentRepository.HaalVoorbeeldenOpAsync(connectionString, "ALLSTARS", NullLogger.Instance);

        await act.Should().ThrowAsync<Exception>();
    }
}
