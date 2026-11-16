using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Admin;
using Xunit;

namespace Planner.Endpoints.Tests.Admin;

public sealed class EmailInstellingenSchemaControleTests
{
    [Fact]
    public async Task AndereInstelling_LeestGeenEmailSchema()
    {
        var gelezen = false;
        var result = await EmailInstellingenSchemaControle.ControleerAsync(
            new Dictionary<string, string?> { ["BufferMinuten"] = "15" },
            () => { gelezen = true; return Task.FromResult(false); }, "synthetic schema message");

        result.Should().BeNull();
        gelezen.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmailWijziging_ControleertSchemaEenmaalEnGeeftAlleenBijOntbreken409(bool beschikbaar)
    {
        var gelezen = 0;
        var result = await EmailInstellingenSchemaControle.ControleerAsync(
            new Dictionary<string, string?> { ["EmailReviewModusActief"] = "true" },
            () => { gelezen++; return Task.FromResult(beschikbaar); }, "synthetic schema message");

        gelezen.Should().Be(1);
        if (beschikbaar) result.Should().BeNull();
        else
        {
            var conflict = result.Should().BeOfType<ConflictObjectResult>().Which;
            conflict.StatusCode.Should().Be(409);
            conflict.Value!.GetType().GetProperty("error")!.GetValue(conflict.Value)
                .Should().Be("synthetic schema message");
        }
    }

    [Fact]
    public async Task OnbekendeSchemaFout_WordtNietAlsSchrijfbaarBeschouwd()
    {
        var act = () => EmailInstellingenSchemaControle.ControleerAsync(
            new Dictionary<string, string?> { ["EmailReviewModusActief"] = "true" },
            () => throw new InvalidOperationException("synthetic read failure"), "synthetic schema message");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
