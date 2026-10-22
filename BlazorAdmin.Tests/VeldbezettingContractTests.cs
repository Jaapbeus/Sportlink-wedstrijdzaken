using System.Text.Json;
using AwesomeAssertions;
using BlazorAdmin.Models;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// #1774 (Codex-review ronde 1, P2): het oude responscontract van <c>GET api/planner/veldbezetting</c> (kale array, gelezen als
/// <c>List&lt;VeldbezettingItemDto&gt;</c> door een reeds geopende of oudere client) moet blijven werken, naast de nieuwe envelope
/// die alleen met <c>?status=true</c> komt en als <see cref="VeldbezettingAntwoordDto"/> wordt gelezen.
/// </summary>
public class VeldbezettingContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string Regel = """{ "wedstrijdCode": 1, "wedstrijd": "A - B", "teamNaam": "A", "aanvangsTijd": "13:00", "veld": "veld 1" }""";

    [Fact]
    public void Oude_client_leest_de_kale_array_van_de_bestaande_route()
    {
        var lijst = JsonSerializer.Deserialize<List<VeldbezettingItemDto>>($"[{Regel}]", Web);

        lijst.Should().NotBeNull();
        lijst!.Should().ContainSingle().Which.Veld.Should().Be("veld 1");
    }

    [Fact]
    public void Oude_client_leest_ook_een_lege_dag_als_lege_array()
        => JsonSerializer.Deserialize<List<VeldbezettingItemDto>>("[]", Web).Should().BeEmpty();

    [Fact]
    public void Nieuwe_client_leest_de_envelope_met_status_en_wedstrijden()
    {
        var json = $$"""{ "sportlink": { "geraadpleegd": false, "reden": "KoppelingVerlopen", "toelichting": "x", "geraadpleegdOpUtc": null, "koppelingHerstelNodig": true }, "wedstrijden": [{{Regel}}] }""";

        var antwoord = JsonSerializer.Deserialize<VeldbezettingAntwoordDto>(json, Web);

        antwoord!.Sportlink!.Geraadpleegd.Should().BeFalse();
        antwoord.Sportlink.KoppelingHerstelNodig.Should().BeTrue();
        antwoord.Wedstrijden.Should().ContainSingle();
    }

    [Fact]
    public void Oude_client_kan_de_envelope_niet_lezen_en_daarom_is_die_alleen_op_aanvraag()
    {
        var act = () => JsonSerializer.Deserialize<List<VeldbezettingItemDto>>("""{ "sportlink": {}, "wedstrijden": [] }""", Web);

        act.Should().Throw<JsonException>();
    }
}
