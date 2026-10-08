using AwesomeAssertions;
using Planner.Shared;
using FunctionApp.Postgres.Planner;
using Xunit;

namespace FunctionApp.Postgres.Tests.Planner;

/// <summary>
/// #1587: een verzoek met een dagdeel levert vensters binnen dat dagdeel op en markeert het antwoord met het
/// gecontroleerde dagdeel. Postgres-tier-tegenhanger: zelfde bestand op de andere tier.
/// </summary>
public class AvailabilityDagdeelVensterTests
{
    private static readonly DateOnly Zaterdag = new(2026, 10, 10);

    private static List<VeldBeschikbaarheidInfo> Velden() => new()
    {
        new() { VeldNummer = 1, BeschikbaarVanaf = new TimeOnly(8, 0), BeschikbaarTot = new TimeOnly(22, 0) }
    };

    private static List<VeldInfo> VeldInfos() => new()
    {
        new() { VeldNummer = 1, VeldNaam = "veld 1", VeldType = "kunstgras" }
    };

    private static CheckAvailabilityResponse Bouw(string? dagdeel, params (string Van, string Tot)[] bezet)
        => AvailabilityService.BuildWindowsResponse(
            Zaterdag, Velden(),
            bezet.Select(b => new BestaandeWedstrijd
            {
                Datum = Zaterdag, VeldNummer = 1,
                AanvangsTijd = TimeOnly.Parse(b.Van), EindTijd = TimeOnly.Parse(b.Tot)
            }).ToList(),
            VeldInfos(), sunset: null, dagdeel);

    [Fact]
    public void Ochtend_GeeftAlleenVenstersBinnenHetOchtendvenster()
    {
        var response = Bouw("ochtend");

        response.GecontroleerdDagdeel.Should().Be("ochtend");
        response.BeschikbareVensters.Should().ContainSingle();
        response.BeschikbareVensters![0].Van.Should().Be("08:30");
        response.BeschikbareVensters[0].Tot.Should().Be("12:00");
    }

    [Fact]
    public void Middag_GeeftAlleenVenstersBinnenHetMiddagvenster()
    {
        var response = Bouw("middag");

        response.GecontroleerdDagdeel.Should().Be("middag");
        response.BeschikbareVensters.Should().ContainSingle();
        response.BeschikbareVensters![0].Van.Should().Be("12:00");
        response.BeschikbareVensters[0].Tot.Should().Be("17:00");
    }

    [Fact]
    public void OchtendVolBezet_GeeftGeenVensters_EnNoemtGeenMiddagvenster()
    {
        // Het veld is de hele ochtend bezet; de middag is vrij, maar die is niet gevraagd.
        var response = Bouw("ochtend", ("08:00", "12:30"));

        response.Beschikbaar.Should().BeFalse();
        response.BeschikbareVensters.Should().BeEmpty();
        response.GecontroleerdDagdeel.Should().Be("ochtend");
    }

    [Fact]
    public void ZonderDagdeel_BlijftDeHeleDagZichtbaar_ZonderGecontroleerdDagdeel()
    {
        var response = Bouw(null);

        response.GecontroleerdDagdeel.Should().BeNull();
        response.BeschikbareVensters.Should().ContainSingle();
        response.BeschikbareVensters![0].Van.Should().Be("08:00");
        response.BeschikbareVensters[0].Tot.Should().Be("22:00");
    }

    [Fact]
    public void OnbekendDagdeel_WordtGenegeerd()
        => Bouw("nacht").GecontroleerdDagdeel.Should().BeNull();
}
