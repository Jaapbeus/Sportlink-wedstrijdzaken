using BlazorAdmin.Services;
using System.Text;
using Xunit;

namespace BlazorAdmin.Tests;

public class TeambegeleidingCsvDecoderTests
{
    [Fact]
    public async Task LeesUtf8Async_ValidUtf8WithBom_DecodesAndRemovesBom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Team;Label\nTeam-1;UTF-8 test ë")).ToArray();
        await using var stream = new MemoryStream(bytes);

        var result = await TeambegeleidingCsvDecoder.LeesUtf8Async(stream);

        Assert.Equal("Team;Label\nTeam-1;UTF-8 test ë", result);
    }

    [Fact]
    public async Task LeesUtf8Async_InvalidUtf8Byte_ThrowsInsteadOfReplacing()
    {
        await using var stream = new MemoryStream([0x54, 0x65, 0x61, 0x6D, 0x3B, 0x4E, 0x61, 0x61, 0x6D, 0x0A, 0x4A, 0x4F, 0x31, 0x33, 0x3B, 0xEB]);

        var read = () => TeambegeleidingCsvDecoder.LeesUtf8Async(stream);

        await Assert.ThrowsAsync<DecoderFallbackException>(read);
    }
}
