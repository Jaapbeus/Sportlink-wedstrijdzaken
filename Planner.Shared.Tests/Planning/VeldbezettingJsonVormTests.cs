using System.Text.Json;
using AwesomeAssertions;
using Planner.Shared.Planning;
using Xunit;

namespace Planner.Shared.Tests.Planning;

/// <summary>
/// #1774 (Codex-review ronde 1, P2): <c>GET /api/planner/veldbezetting</c> behoudt zonder opt-in de kale array (oud contract);
/// alleen <c>?status=true</c> geeft de envelope <c>{ sportlink, wedstrijden }</c>. Beide tiers gebruiken dezelfde twee helpers.
/// </summary>
public class VeldbezettingJsonVormTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static VeldbezettingAntwoord<string> Antwoord()
        => new(SportlinkVeldplannerStatus.Terugval(SportlinkVeldplannerStatus.RedenKoppelingVerlopen), new[] { "a", "b" });

    [Fact]
    public void Zonder_opt_in_blijft_het_oude_contract_een_kale_array()
    {
        var json = JsonSerializer.Serialize(Antwoord().JsonVorm(false), Web);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Lege_dag_zonder_opt_in_blijft_een_lege_array()
    {
        var leeg = new VeldbezettingAntwoord<string>(SportlinkVeldplannerStatus.Terugval(SportlinkVeldplannerStatus.RedenNetwerkFout), Array.Empty<string>());

        JsonSerializer.Serialize(leeg.JsonVorm(false), Web).Should().Be("[]");
    }

    [Fact]
    public void Met_opt_in_komt_de_envelope_met_status_en_wedstrijden()
    {
        var json = JsonSerializer.Serialize(Antwoord().JsonVorm(true), Web);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.GetProperty("wedstrijden").GetArrayLength().Should().Be(2);
        var status = doc.RootElement.GetProperty("sportlink");
        status.GetProperty("geraadpleegd").GetBoolean().Should().BeFalse();
        status.GetProperty("reden").GetString().Should().Be(SportlinkVeldplannerStatus.RedenKoppelingVerlopen);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    [InlineData("1", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StatusAangevraagd_is_alleen_waar_voor_de_waarde_true(string? waarde, bool verwacht)
        => VeldbezettingAntwoord<string>.StatusAangevraagd(waarde).Should().Be(verwacht);
}
