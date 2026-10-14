using Planner.Shared;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

// Testimplementaties van de poorten van BerichtAntwoordGenerator (#1758). Zelfde vorm en standaardwaarden als de
// tier-DTO's (CheckAvailabilityResponse, BeschikbaarVenster, ZoekWedstrijdResponse, HerplanCheckResponse,
// InkomendBericht, EmailTemplate, ClubAppSettingsSnapshot), zodat de gevallen hier gelden voor beide tiers.

/// <summary>
/// De generatortests zetten soms de procesbrede omgevingsvariabele <c>EmailReviewMode</c>; daarom draaien ze in
/// één collectie zonder parallelle uitvoering.
/// </summary>
[CollectionDefinition(Naam, DisableParallelization = true)]
public sealed class BerichtAntwoordCollectie
{
    public const string Naam = "BerichtAntwoordGenerator";
}

internal sealed class TestBeschikbaarheid : IAntwoordBeschikbaarheid
{
    public bool Beschikbaar { get; set; }
    public SlotToewijzing? Toewijzing { get; set; }
    public bool HeeftTeamConflict { get; set; }
    public string? Reden { get; set; }
    public string? GecontroleerdDagdeel { get; set; }
    public List<SlotToewijzing> Alternatieven { get; set; } = new();
    public List<TestVenster>? BeschikbareVensters { get; set; }
    public List<string> Waarschuwingen { get; set; } = new();

    IReadOnlyList<IAntwoordVenster>? IAntwoordBeschikbaarheid.BeschikbareVensters => BeschikbareVensters;
}

internal sealed class TestVenster : IAntwoordVenster
{
    public int VeldNummer { get; set; }
    public string VeldNaam { get; set; } = string.Empty;
    public string Van { get; set; } = string.Empty;
    public string Tot { get; set; } = string.Empty;
    public int MaxDuurMinuten { get; set; }
    public string? Opmerking { get; set; }
    public string? VeldType { get; set; }
}

internal sealed class TestWedstrijd : IAntwoordWedstrijd
{
    public long Wedstrijdcode { get; set; }
    public string Wedstrijd { get; set; } = string.Empty;
    public string Datum { get; set; } = string.Empty;
    public string AanvangsTijd { get; set; } = string.Empty;
    public string EindTijd { get; set; } = string.Empty;
    public string? VeldNaam { get; set; }
}

internal sealed class TestHerplanOpties : IAntwoordHerplanOpties
{
    public bool Beschikbaar { get; set; }
    public List<SlotToewijzing> Alternatieven { get; set; } = new();
}

internal sealed class TestAfzender : IAntwoordAfzender
{
    public string Afzender { get; set; } = "trainer@voorbeeld.nl";
    public string AfzenderNaam { get; set; } = "Jan de Vries";
    public string Onderwerp { get; set; } = "Beschikbaarheid";
}

internal sealed record TestSjabloon(string Key, string Onderwerp, string Body) : IAntwoordSjabloon;

internal sealed class TestInstellingen : IAntwoordInstellingen
{
    public string? EmailVoetnoot { get; set; }
    public string? PlannerAfzenderNaam { get; set; }
    public string? CoordinatorNaam { get; set; }
    public string? CoordinatorFunctie { get; set; }
    public string OntbrekendeAfzenderNaamMelding { get; set; } = "testmelding: afzendernaam ontbreekt";

    /// <summary>Voetnoot-only: houdt de handtekening buiten tests die over de inhoud gaan (zie #677).</summary>
    public static TestInstellingen AlleenVoetnoot() => new() { EmailVoetnoot = "Testomgeving" };
}
