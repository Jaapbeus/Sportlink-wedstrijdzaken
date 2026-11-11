using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

public class GraphRestartBaselineTests
{
    [Fact]
    public async Task ExternalMailMustProgressAcrossEveryPollRestart()
    {
        var previous = Environment.GetEnvironmentVariable("GraphMailbox");
        Environment.SetEnvironmentVariable("GraphMailbox", "mailbox@allstars-fc.test");
        try
        {
            using var handler = new Inbox();
            using var http = new HttpClient(handler);
            using var graph = new GraphServiceClient(http, new AnonymousAuthenticationProvider());
            var delivered = new List<string>();
            for (var poll = 0; poll < 2; poll++)
            {
                var service = new EmailGraphService(graph, NullLogger<EmailGraphService>.Instance);
                var fetched = await service.GetUnreadEmailsAsync();
                var eligible = await new EmailBatchFilterService().PreFilterVoorClassificatieAsync(
                    fetched, "mailbox@allstars-fc.test", new HashSet<string>(), service, NullLogger.Instance);
                delivered.AddRange(eligible.Select(m => m.MessageId));
            }
            handler.ReadCounts.Should().Equal(0, 10);
            handler.ReadIds.Should().HaveCount(10);
            delivered.Should().Contain("external", "the retained existing own-mail read policy progresses after restarting the service");
        }
        finally { Environment.SetEnvironmentVariable("GraphMailbox", previous); }
    }

    private sealed class Inbox : HttpMessageHandler
    {
        public List<int> ReadCounts { get; } = [];
        public HashSet<string> ReadIds { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            uri.Host.Should().Be("graph.microsoft.com");
            if (request.Method == HttpMethod.Patch)
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                payload.RootElement.GetProperty("isRead").GetBoolean().Should().BeTrue();
                var id = uri.Segments.Last();
                id.Should().StartWith("alert-");
                ReadIds.Add(id).Should().BeTrue();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            request.Method.Should().Be(HttpMethod.Get);
            ReadCounts.Add(ReadIds.Count);
            var messages = Enumerable.Range(0, 10)
                .Where(i => !ReadIds.Contains("alert-" + i))
                .Select(i => Message("alert-" + i, "mailbox@allstars-fc.test", "uRgEnT: alert"))
                .Append(Message("external", "trainer@voorbeeld.nl", "URGENT: external request"))
                .Take(10).ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { value = messages }), Encoding.UTF8, "application/json") };
        }
        private static object Message(string id, string address, string subject)
            => new { id, subject, from = new { emailAddress = new { address } }, body = new { content = "Verzoek", contentType = "text" } };
    }
}
