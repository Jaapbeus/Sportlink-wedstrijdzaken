using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Http;
using System.Text;
using Xunit;

namespace Planner.Endpoints.Tests.Http;

public class BoundedRequestBodyReaderTests
{
    [Fact]
    public async Task ReadUtf8_ExactLimit_AcceptsBodyAcrossShortReads()
    {
        var stream = new CountingStream(Encoding.UTF8.GetBytes("hello"), maxChunkSize: 2);

        var result = await BoundedRequestBodyReader.ReadUtf8Async(Request(stream), 5);

        result.IsSuccess.Should().BeTrue();
        result.Text.Should().Be("hello");
        stream.BytesRead.Should().Be(5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1L)]
    public async Task ReadUtf8_OverLimit_CountsObservedBytesEvenWithoutReliableLength(long? contentLength)
    {
        var stream = new CountingStream(Encoding.UTF8.GetBytes("abcdef"));

        var result = await BoundedRequestBodyReader.ReadUtf8Async(Request(stream, contentLength), 5);

        result.Error.Should().Be(RequestBodyReadError.TooLarge);
        result.Text.Should().BeNull();
        stream.BytesRead.Should().Be(6);
    }

    [Fact]
    public async Task ReadUtf8_DeclaredTooLarge_RejectsBeforeReading()
    {
        var stream = new CountingStream(Encoding.UTF8.GetBytes("small"));

        var result = await BoundedRequestBodyReader.ReadUtf8Async(Request(stream, 6), 5);

        result.Error.Should().Be(RequestBodyReadError.TooLarge);
        stream.BytesRead.Should().Be(0);
    }

    [Fact]
    public async Task ReadUtf8_ExactMultibyteLimit_AcceptsValidUtf8()
    {
        var stream = new CountingStream(Encoding.UTF8.GetBytes("é"));

        var result = await BoundedRequestBodyReader.ReadUtf8Async(Request(stream), 2);

        result.IsSuccess.Should().BeTrue();
        result.Text.Should().Be("é");
        stream.BytesRead.Should().Be(2);
    }

    [Fact]
    public async Task ReadUtf8_InvalidUtf8_ReturnsFixedFailureWithoutText()
    {
        var stream = new CountingStream([0xC3, 0x28]);

        var result = await BoundedRequestBodyReader.ReadUtf8Async(Request(stream), 8);

        result.Error.Should().Be(RequestBodyReadError.InvalidUtf8);
        result.Text.Should().BeNull();
        stream.BytesRead.Should().Be(2);
    }

    [Fact]
    public async Task ReadUtf8_EmptyBody_ReturnsEmptyText()
    {
        var result = await BoundedRequestBodyReader.ReadUtf8Async(Request(new CountingStream([])), 4);

        result.IsSuccess.Should().BeTrue();
        result.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadUtf8_Cancellation_IsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var request = Request(new CountingStream(Encoding.UTF8.GetBytes("body")));
        request.HttpContext.RequestAborted = cancellation.Token;

        await FluentActions.Awaiting(() => BoundedRequestBodyReader.ReadUtf8Async(request, 10))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadJson_InvalidJson_ReturnsTypedFailure()
    {
        var stream = new CountingStream(Encoding.UTF8.GetBytes("{broken"));

        var result = await BoundedRequestBodyReader.ReadJsonAsync<JsonFixture>(Request(stream), 32);

        result.Error.Should().Be(RequestBodyReadError.InvalidJson);
        result.Value.Should().BeNull();
    }

    [Fact]
    public void ToActionResult_DoesNotEchoBodyAndUsesFixedStatuses()
    {
        var tooLarge = BoundedRequestBodyReader.ToActionResult(RequestBodyReadError.TooLarge) as ObjectResult;
        var invalid = BoundedRequestBodyReader.ToActionResult(RequestBodyReadError.InvalidUtf8) as BadRequestObjectResult;

        tooLarge!.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        tooLarge.Value.Should().NotBeNull();
        Newtonsoft.Json.JsonConvert.SerializeObject(tooLarge.Value).Should().NotContain("secret");
        invalid.Should().NotBeNull();
        Newtonsoft.Json.JsonConvert.SerializeObject(invalid!.Value).Should().NotContain("secret");
    }

    private static HttpRequest Request(CountingStream body, long? contentLength = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = body;
        context.Request.ContentLength = contentLength;
        return context.Request;
    }

    private sealed class JsonFixture
    {
        public string? Value { get; set; }
    }

    private sealed class CountingStream(byte[] content, int maxChunkSize = int.MaxValue) : MemoryStream(content)
    {
        public long BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = base.Read(buffer.Span[..Math.Min(buffer.Length, maxChunkSize)]);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }
}
