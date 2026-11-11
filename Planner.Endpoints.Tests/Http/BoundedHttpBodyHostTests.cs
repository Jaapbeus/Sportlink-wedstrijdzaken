using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using KestrelServerOptions = Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Http;
using Xunit;

namespace Planner.Endpoints.Tests.Http;

/// <summary>Real loopback Kestrel; no Functions host, database or external integration.</summary>
public class BoundedHttpBodyHostTests
{
    [Fact]
    public async Task DefaultKestrel_RejectsExistingEscapedGuiEnvelopeBeforeCallback()
    {
        await using var server = await Server.StartAsync(configured: false);
        using var content = new ByteArrayContent(Envelope());
        try
        {
            using var response = await server.Client.PostAsync("/baseline", content);
            response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        }
        catch (HttpRequestException)
        {
            // Kestrel may close the socket while the client is still uploading the rejected body.
        }
        server.Calls.Should().Be(0);
        server.ServerReadFailures.Should().Be(1);
        server.ServerReadFailureStatus.Should().Be(StatusCodes.Status413PayloadTooLarge);
        server.ServerLimit.Should().Be(30_000_000);
    }

    [Fact]
    public async Task ConfiguredKestrel_ExactGuiEnvelopeLimit_ReachesCallback()
    {
        await using var server = await Server.StartAsync(configured: true);
        using var content = Content(Envelope(TeambegeleidingImportRequestBodyLimits.MaxJsonBytes), chunked: false);
        using var response = await server.SendAsync("/import", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        server.Calls.Should().Be(1);
        server.ServerReadFailures.Should().Be(0);
        server.CsvLength.Should().Be(TeambegeleidingImportRequestBodyLimits.CsvFileMaxBytes);
        server.FilenameLength.Should().Be(TeambegeleidingImportRequestBodyLimits.AuditFilenameMaxCharacters);
    }

    [Fact]
    public void FunctionsRegistration_UsesFinitePlatformTransportCeiling()
    {
        var builder = FunctionsApplication.CreateBuilder([]);
        builder.ConfigureBoundedFunctionsWebApplication();
        using var services = builder.Services.BuildServiceProvider();
        services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize
            .Should().Be(220_200_960);
    }

    [Fact]
    public async Task ConfiguredKestrel_ChunkedGuiUploadBelowWireCeiling_ReachesCallback()
    {
        await using var server = await Server.StartAsync(configured: true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            csvContent = new string('a', TeambegeleidingImportRequestBodyLimits.CsvFileMaxBytes),
            bestandsnaam = new string('a', TeambegeleidingImportRequestBodyLimits.AuditFilenameMaxCharacters)
        });
        using var content = Content(bytes, chunked: true);
        using var response = await server.SendAsync("/import", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        server.Calls.Should().Be(1);
        server.CsvLength.Should().Be(TeambegeleidingImportRequestBodyLimits.CsvFileMaxBytes);
        server.FilenameLength.Should().Be(TeambegeleidingImportRequestBodyLimits.AuditFilenameMaxCharacters);
    }

    [Fact]
    public async Task ChunkFramingAtApplicationLimit_RemainsWithinTransportCeiling()
    {
        await using var server = await Server.StartAsync(configured: true);
        using var content = Content(Envelope(TeambegeleidingImportRequestBodyLimits.MaxJsonBytes), chunked: true);
        using var response = await server.SendAsync("/import", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        server.Calls.Should().Be(1);
        server.ServerReadFailures.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredKestrel_OverEnvelope_ReturnsControlled413BeforeCallback(bool chunked)
    {
        await using var server = await Server.StartAsync(configured: true);
        using var content = Content(Envelope(TeambegeleidingImportRequestBodyLimits.MaxJsonBytes + 1), chunked);
        using var response = await server.SendAsync("/import", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        server.Calls.Should().Be(0);
        server.ServerReadFailures.Should().Be(0, "the bounded reader must translate the server's 413 instead of leaking it to the worker bridge");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredKestrel_SmallerRouteLimitStillRejectsBeforeCallback(bool chunked)
    {
        await using var server = await Server.StartAsync(configured: true);
        using var content = Content(new byte[8193], chunked);
        using var response = await server.SendAsync("/small", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        server.Calls.Should().Be(0);
        server.ServerReadFailures.Should().Be(0);
    }

    private static byte[] Envelope(int? paddedLength = null)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            CsvContent = new string('\0', TeambegeleidingImportRequestBodyLimits.CsvFileMaxBytes),
            Bestandsnaam = new string('\0', TeambegeleidingImportRequestBodyLimits.AuditFilenameMaxCharacters)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Length.Should().BeGreaterThan(30_000_000);
        if (paddedLength is not { } length) return json;
        var result = new byte[length];
        result.AsSpan().Fill((byte)' ');
        json.CopyTo(result, 0);
        return result;
    }

    private static HttpContent Content(byte[] bytes, bool chunked)
        => chunked ? new ChunkedContent(bytes) : new ByteArrayContent(bytes);

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var offset = 0; offset < bytes.Length; offset += 8191)
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(8191, bytes.Length - offset)));
        }
    }

    private sealed class Server(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public int Calls { get; private set; }
        public long? ServerLimit { get; private set; }
        public int CsvLength { get; private set; }
        public int FilenameLength { get; private set; }
        public int ServerReadFailures { get; private set; }
        public int? ServerReadFailureStatus { get; private set; }

        public static async Task<Server> StartAsync(bool configured)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            if (configured) builder.Services.ConfigureBoundedHttpBodies();
            var app = builder.Build();
            var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var server = new Server(app, client);
            app.Run(server.HandleAsync);
            await app.StartAsync();
            client.BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return server;
        }

        public async Task<HttpResponseMessage> SendAsync(string path, HttpContent content)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            request.Headers.ExpectContinue = true;
            return await client.SendAsync(request);
        }

        private async Task HandleAsync(HttpContext context)
        {
            try
            {
                ServerLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize;
                if (context.Request.Path == "/baseline")
                {
                    await context.Request.Body.CopyToAsync(Stream.Null);
                    Calls++;
                    return;
                }
                var limit = context.Request.Path == "/small" ? 8192 : TeambegeleidingImportRequestBodyLimits.MaxJsonBytes;
                var result = await BoundedRequestBodyReader.ExecuteJsonAsync<Dictionary<string, string>>(
                    context.Request, value =>
                    {
                        Calls++;
                        CsvLength = value!["csvContent"].Length;
                        FilenameLength = value["bestandsnaam"].Length;
                        return Task.FromResult<IActionResult>(new OkResult());
                    }, limit);
                context.Response.StatusCode = ((IStatusCodeActionResult)result).StatusCode!.Value;
            }
            catch (BadHttpRequestException ex)
            {
                ServerReadFailures++;
                ServerReadFailureStatus = ex.StatusCode;
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
