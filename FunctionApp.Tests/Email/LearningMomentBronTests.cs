using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

public class LearningMomentBronTests
{
    [Theory]
    [InlineData("unsupported=value")]
    [InlineData("Server=127.0.0.1,1;Connect Timeout=1;Encrypt=False;Pooling=False")]
    public async Task OnbeschikbareBron_Propageert_EnIsGeenLegeVoorbeeldset(string connectionString)
    {
        var act = () => LearningMomentRepository.HaalVoorbeeldenOpAsync("ALLSTARS", NullLogger.Instance, connectionString);

        await act.Should().ThrowAsync<Exception>();
    }
}
