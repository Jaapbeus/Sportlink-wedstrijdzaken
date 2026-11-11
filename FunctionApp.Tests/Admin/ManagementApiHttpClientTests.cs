using AwesomeAssertions;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

public class ManagementApiHttpClientTests
{
    [Fact]
    public async Task SqlServerManagementClientHasBoundedRequestTimeout()
    {
        var handler = new WaitingHandler();
        using var client = AdminSettingsFunction.CreateManagementHttpClient(handler);

        client.Timeout.Should().Be(TimeSpan.FromSeconds(10));
        client.Timeout = TimeSpan.FromMilliseconds(50);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var send = () => client.GetAsync("http://localhost/management", cancellation.Token);
        var exception = await Record.ExceptionAsync(send);
        exception.Should().BeAssignableTo<OperationCanceledException>();
        exception!.InnerException.Should().BeOfType<TimeoutException>();
        handler.CancellationObserved.Should().BeTrue();
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => CancellationObserved = true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
