using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Planner.Endpoints.Http;

public enum RequestBodyReadError
{
    None,
    TooLarge,
    InvalidUtf8,
    InvalidJson
}

public sealed record RequestBodyReadResult(string? Text, RequestBodyReadError Error)
{
    public bool IsSuccess => Error == RequestBodyReadError.None;
}

public sealed record JsonRequestBodyReadResult<T>(T? Value, RequestBodyReadError Error)
{
    public bool IsSuccess => Error == RequestBodyReadError.None;
}

public sealed class RequestBodyReadException(RequestBodyReadError error) : Exception
{
    public RequestBodyReadError Error { get; } = error;
}

/// <summary>Reads a bounded UTF-8 HTTP body before it is decoded or parsed.</summary>
public static class BoundedRequestBodyReader
{
    public const int DefaultJsonMaxBytes = 1024 * 1024;
    public const string TooLargeMessage = "Request body is te groot.";
    public const string InvalidJsonMessage = "Request body is geen geldige UTF-8 JSON.";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<RequestBodyReadResult> ReadUtf8Async(HttpRequest request, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        if (maxBytes == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (request.ContentLength is { } declaredLength && declaredLength > maxBytes)
            return new(null, RequestBodyReadError.TooLarge);

        var buffer = ArrayPool<byte>.Shared.Rent(maxBytes + 1);
        try
        {
            var totalRead = 0;
            var cancellationToken = request.HttpContext.RequestAborted;
            while (totalRead < maxBytes + 1)
            {
                var read = await request.Body.ReadAsync(
                    buffer.AsMemory(totalRead, maxBytes + 1 - totalRead), cancellationToken);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead > maxBytes)
                return new(null, RequestBodyReadError.TooLarge);

            try
            {
                // StreamReader accepted a leading UTF-8 BOM. Its bytes still count toward the limit.
                var offset = totalRead >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF
                    ? 3 : 0;
                return new(StrictUtf8.GetString(buffer, offset, totalRead - offset), RequestBodyReadError.None);
            }
            catch (DecoderFallbackException)
            {
                return new(null, RequestBodyReadError.InvalidUtf8);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public static async Task<JsonRequestBodyReadResult<T>> ReadJsonAsync<T>(HttpRequest request, int maxBytes)
        where T : class
    {
        var body = await ReadUtf8Async(request, maxBytes);
        if (!body.IsSuccess)
            return new(null, body.Error);

        try
        {
            return new(JsonConvert.DeserializeObject<T>(body.Text!), RequestBodyReadError.None);
        }
        catch (JsonException)
        {
            return new(null, RequestBodyReadError.InvalidJson);
        }
    }

    /// <summary>Runs an endpoint action only after a bounded body has been read successfully.</summary>
    public static async Task<IActionResult> ExecuteUtf8Async(
        HttpRequest request, Func<string?, Task<IActionResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var body = await ReadUtf8Async(request, DefaultJsonMaxBytes);
        return body.IsSuccess ? await action(body.Text) : ToActionResult(body.Error);
    }

    public static IActionResult ToActionResult(
        RequestBodyReadError error,
        string tooLargeMessage = TooLargeMessage,
        string invalidMessage = InvalidJsonMessage)
    {
        var message = error switch
        {
            RequestBodyReadError.TooLarge => tooLargeMessage,
            RequestBodyReadError.InvalidUtf8 or RequestBodyReadError.InvalidJson => invalidMessage,
            _ => throw new ArgumentOutOfRangeException(nameof(error))
        };
        return error == RequestBodyReadError.TooLarge
            ? new ObjectResult(new { error = message }) { StatusCode = StatusCodes.Status413PayloadTooLarge }
            : new BadRequestObjectResult(new { error = message });
    }
}
