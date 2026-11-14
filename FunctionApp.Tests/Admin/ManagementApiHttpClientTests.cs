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
        private int cancellationObserved;

        public bool CancellationObserved => Volatile.Read(ref cancellationObserved) == 1;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // #1941: de vlag staat vast vóórdat de annulering de handler verlaat. Een losse
                // cancellationToken.Register-callback liep soms pas na de aanroeper (callbacks lopen in
                // omgekeerde registratievolgorde, Task.Delay registreert later), wat de test flaky maakte.
                Volatile.Write(ref cancellationObserved, 1);
                throw;
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
