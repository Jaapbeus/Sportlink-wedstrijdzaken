using AwesomeAssertions;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

public class AzureManagementHttpClientFactoryTests
{
    [Fact]
    public async Task ClientTimeout_CancelsPendingFakeRequest()
    {
        var handler = new WaitingHandler();
        using var client = AzureManagementHttpClientFactory.Create(TimeSpan.FromMilliseconds(50), handler);

        var send = () => client.GetAsync("http://localhost/management");
        var exception = await Record.ExceptionAsync(send);

        exception.Should().BeAssignableTo<OperationCanceledException>();
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult(true));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
