using System.Text;

namespace BlazorAdmin.Services;

/// <summary>Reads UTF-8 CSV input without silently replacing malformed byte sequences.</summary>
public static class TeambegeleidingCsvDecoder
{
    public static async Task<string> LeesUtf8Async(Stream stream)
    {
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        var content = await reader.ReadToEndAsync();
        return content.StartsWith('\uFEFF') ? content[1..] : content;
    }
}
