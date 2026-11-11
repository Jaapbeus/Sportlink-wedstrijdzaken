using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using FunctionApp.Postgres.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

public class LearningMomentBronTests
{
    [Theory]
    [InlineData("unsupported=value")]
    [InlineData("Host=127.0.0.1;Port=1;Username=allstars;Timeout=1;Pooling=false")]
    public async Task OnbeschikbareBron_Propageert_EnIsGeenLegeVoorbeeldset(string connectionString)
    {
        var act = () => LearningMomentRepository.HaalVoorbeeldenOpAsync(connectionString, "ALLSTARS", NullLogger.Instance);

        await act.Should().ThrowAsync<Exception>();
    }
}
