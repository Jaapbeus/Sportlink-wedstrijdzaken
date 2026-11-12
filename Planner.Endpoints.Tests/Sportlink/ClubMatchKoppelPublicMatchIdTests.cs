using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Endpoints.Tests.Sportlink;

/// <summary>#1879: het echte PublicMatchId van een geslaagde aanmaak komt op de audit-rij.</summary>
public class ClubMatchKoppelPublicMatchIdTests
{
    private static SportlinkMutationResult Resultaat(
        bool succes = true, bool dryRun = false, bool geforceerdDryRun = false, string? id = "synthetic-match-id")
        => new(succes, null, dryRun, geforceerdDryRun, id);

    private static async Task<List<string>> Roep(SportlinkMutationResult? resultaat, bool zetGeeftTrue = true)
    {
        var aanroepen = new List<string>();
        await ClubMatchEndpointCore.KoppelPublicMatchIdAsync(resultaat, id =>
        {
            aanroepen.Add(id);
            return Task.FromResult(zetGeeftTrue);
        });
        return aanroepen;
    }

    [Fact]
    public async Task GeslaagdeEchteAanmaak_ZetHetEchteId()
    {
        var aanroepen = await Roep(Resultaat());

        aanroepen.Should().Equal("synthetic-match-id");
    }

    [Fact]
    public void Placeholder_IsDeVasteWaardeNieuw()
    {
        ClubMatchEndpointCore.AuditPublicMatchIdPlaceholder.Should().Be("NIEUW");
    }

    [Fact]
    public async Task Mislukking_ZetNiets()
    {
        (await Roep(Resultaat(succes: false))).Should().BeEmpty();
    }

    [Fact]
    public async Task DryRun_ZetNiets()
    {
        (await Roep(Resultaat(dryRun: true))).Should().BeEmpty();
    }

    [Fact]
    public async Task GeforceerdeDryRun_ZetNiets()
    {
        (await Roep(Resultaat(geforceerdDryRun: true))).Should().BeEmpty();
    }

    [Fact]
    public async Task GeenResultaat_ZetNiets()
    {
        (await Roep(null)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("heeft spatie")]
    [InlineData("pad/../id")]
    [InlineData("synthetic-id\n")]      // afsluitende LF: een regex met $ zou deze doorlaten
    [InlineData("synthetic-id\r\n")]    // afsluitende CRLF
    [InlineData("synthetic\nid")]       // ingebedde LF
    [InlineData("synthetic-id\t")]
    [InlineData("synthetic-id\0")]
    [InlineData("syntheticé")]          // niet-ASCII letter
    public async Task OngeldigId_ZetNiets(string? id)
    {
        (await Roep(Resultaat(id: id))).Should().BeEmpty();
    }

    [Fact]
    public async Task IdLangerDanDeAuditKolom_ZetNiets()
    {
        // De audit-kolom is 50 tekens; de ID-vorm laat er 64 toe.
        (await Roep(Resultaat(id: new string('a', 51)))).Should().BeEmpty();
        (await Roep(Resultaat(id: new string('a', 50)))).Should().HaveCount(1);
    }

    [Fact]
    public async Task GeenBijgewerkteRij_GooitNiet()
    {
        var aanroepen = await Roep(Resultaat(), zetGeeftTrue: false);

        aanroepen.Should().HaveCount(1);
    }

    [Fact]
    public async Task FoutBijHetBijwerken_BreektDeRespons_Niet()
    {
        var act = () => ClubMatchEndpointCore.KoppelPublicMatchIdAsync(
            Resultaat(), _ => throw new InvalidOperationException("synthetische databasefout"), NullLogger.Instance);

        await act.Should().NotThrowAsync();
    }
}
