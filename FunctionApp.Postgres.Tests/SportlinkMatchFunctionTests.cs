using FluentAssertions;
using FunctionApp.Postgres.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Unit tests voor de pure helperlogica in <see cref="SportlinkMatchFunction"/> (#992, epic #986).
/// Geen database nodig — <see cref="SportlinkMatchFunction.BouwKleedkamerId"/> is een pure
/// string-transformatie.
/// </summary>
public class SportlinkMatchFunctionTests
{
    [Fact]
    public void BouwKleedkamerId_MetNummerEnFacilityId_BouwtDeEchteSportlinkIdentifier()
    {
        // Live vastgesteld (2026-09-06, netwerktrace door de eigenaar): Sportlink verwacht
        // "{FacilityId}-DRESSINGROOM-{n}", bijv. "BBCF989-DRESSINGROOM-11" — een los kleedkamernummer
        // ("10") werd afgewezen met INVALID_COMBINATION_FACILITY_DRESSINGROOM (#1040/#992).
        var result = SportlinkMatchFunction.BouwKleedkamerId("BBCF989", "11");

        result.Should().Be("BBCF989-DRESSINGROOM-11");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BouwKleedkamerId_LeegOfNullKleedkamerNummer_GeeftDatOngewijzigdTerug(string? leeg)
    {
        // Sportlinks eigen UI stuurt een lege string voor een niet-toegewezen kleedkamer (bevestigd
        // in dezelfde netwerktrace: "AwayDressingRoomId":""), niet null. Geen facility-prefix nodig
        // voor "geen kleedkamer".
        var result = SportlinkMatchFunction.BouwKleedkamerId("BBCF989", leeg);

        result.Should().Be(leeg);
    }

    [Fact]
    public void BouwKleedkamerId_GeenFacilityIdBekend_GeeftRuweNummerTerugAlsFallback()
    {
        // Defensief pad: als de match-respons onverwacht geen FacilityId bevat, is een onveranderd
        // doorgeven van het ruwe nummer beter dan een crash — Sportlink zal het dan zelf afwijzen,
        // net als vóór deze fix.
        var result = SportlinkMatchFunction.BouwKleedkamerId(null, "11");

        result.Should().Be("11");
    }

    [Theory]
    [InlineData(true, false, "DryRun")]
    [InlineData(false, true, "Success")]
    [InlineData(false, false, "Failure")]
    public void BepaalAuditResultaat_GeeftJuisteAuditwaarde(bool isDryRun, bool isSuccess, string verwacht)
    {
        // #998: DryRun gaat vóór IsSuccess — bij een dry-run-aanroep is IsSuccess altijd true
        // (gesimuleerd succes), maar de audit moet expliciet tonen dat er niets echt is verzonden.
        var result = new SportlinkMutationResult(isSuccess, Violations: null, IsDryRun: isDryRun);

        var resultaat = SportlinkMatchFunction.BepaalAuditResultaat(result);

        resultaat.Should().Be(verwacht);
    }
}
