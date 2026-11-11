using Planner.Shared;
using Planner.Shared.Deel;
using Planner.Shared.Email;

// Gedeelde bron voor de planner-wire-DTO's (#1856): één bestand, door beide databasetiers
// link-gecompileerd (zie docs/ARCHITECTUUR-CODEKWALITEIT.md, regel 2b). Wijzig je iets hier, dan
// wijzigt het op SQL Server én Postgres tegelijk; de naamruimte per tier blijft die van de tier.
// Endpoints: check-availability, doordeweeks-beschikbaar en herplan-check.
#if PLANNER_DTO_POSTGRES
namespace FunctionApp.Postgres.Planner;
#else
namespace SportlinkFunction.Planner;
#endif

// ── Aanvraag ──

public class CheckAvailabilityRequest
{
    public string Datum { get; set; } = string.Empty;
    public string? AanvangsTijd { get; set; }
    public string? Dagdeel { get; set; } // "ochtend", "middag", "avond"
    public string? LeeftijdsCategorie { get; set; }
    public string? TeamNaam { get; set; }
    public string? Tegenstander { get; set; }
    public int? WedstrijdDuurMinuten { get; set; }
    // Heel veld gevraagd — overschrijft de veldafmeting uit Speeltijden (bijv. JO12 op heel veld i.p.v. halftijdsspeelveld)
    public bool? HeelVeld { get; set; }
}

// ── Antwoord ──

public class CheckAvailabilityResponse : IAntwoordBeschikbaarheid
{
    public bool Beschikbaar { get; set; }
    public SlotToewijzing? Toewijzing { get; set; }
    public TeamConflictInfo? TeamConflict { get; set; }
    public string? Reden { get; set; }
    /// <summary>
    /// Het dagdeel ("ochtend", "middag", "avond") waarop de vensters en alternatieven zijn beperkt, of null als
    /// er geen dagdeel is toegepast (#1587). Het antwoord aan de afzender vermeldt dit, zodat duidelijk is dat
    /// andere dagdelen niet zijn gecontroleerd.
    /// </summary>
    public string? GecontroleerdDagdeel { get; set; }
    public List<SlotToewijzing> Alternatieven { get; set; } = new();
    public List<BeschikbaarVenster>? BeschikbareVensters { get; set; }
    public List<string> Waarschuwingen { get; set; } = new();

    // Poort van de gedeelde antwoordgenerator (#1758); expliciet, dus geen extra JSON-veld.
    bool IAntwoordBeschikbaarheid.HeeftTeamConflict => TeamConflict != null;
    IReadOnlyList<IAntwoordVenster>? IAntwoordBeschikbaarheid.BeschikbareVensters => BeschikbareVensters;
}

// SlotToewijzing verhuisd naar Planner.Shared (#888) — de scheduling-engine construeert hem
// rechtstreeks, dus moest hij op de gedeelde locatie staan. using Planner.Shared; hierboven.

public class TeamConflictInfo
{
    public string Wedstrijd { get; set; } = string.Empty;
    public string AanvangsTijd { get; set; } = string.Empty;
    public string EindTijd { get; set; } = string.Empty;
    public string VeldNaam { get; set; } = string.Empty;
}

public class BeschikbaarVenster : IAntwoordVenster
{
    public int VeldNummer { get; set; }
    public string VeldNaam { get; set; } = string.Empty;
    public string Van { get; set; } = string.Empty;
    public string Tot { get; set; } = string.Empty;
    public int MaxDuurMinuten { get; set; }
    public string? Opmerking { get; set; }

    /// <summary>Veldtype uit <c>dbo.Velden</c>; <c>null</c> = onbekend. Zie <see cref="SlotToewijzing.VeldType"/> (#705).</summary>
    public string? VeldType { get; set; }
}

// ── Interne modellen ──
//
// Speeltijd, VeldInfo, VeldSoort, VeldTypeClassificatie, VeldBeschikbaarheidInfo,
// BestaandeWedstrijd, TeamRegel en TeamVoorkeurVeld zijn verhuisd naar
// Planner.Shared/PlannerDomeinModellen.cs (#888) — de scheduling-engine (FieldScheduler,
// eveneens verhuisd) werkt uitsluitend op deze modellen en heeft geen SQL-afhankelijkheid;
// zie de klasse-doc-comment daar voor de volledige redenering. using Planner.Shared; hierboven.

// ── Optimalisatie modellen ──
// OptimaliseerRequest/Response en VeldCapaciteitInfo zijn vervallen bij #666, samen met het
// endpoint /planner/optimaliseer. OptimalisatieSuggestie is bij issue 888 vervolg (§42) mee
// verhuisd naar Planner.Shared/PlannerDomeinModellen.cs, samen met PlannerHtmlGenerator dat het
// type als invoermodel gebruikt — using Planner.Shared; hierboven.

// ── Doordeweeks beschikbaarheid modellen ──

public class DoordeweeksBeschikbaarRequest
{
    public string? DagFilter { get; set; }  // "maandag", "dinsdag", "woensdag", "donderdag" of null voor alle
    public int? DuurMinuten { get; set; }
    public string? LeeftijdsCategorie { get; set; }
}

public class DoordeweeksBeschikbaarResponse
{
    public List<DoordeweekseDatum> BeschikbareDatums { get; set; } = new();
    public string? DagFilter { get; set; }
    public string SeizoenEinde { get; set; } = string.Empty;
    public int AantalBeschikbaar { get; set; }
}

public class DoordeweekseDatum
{
    public string Datum { get; set; } = string.Empty;
    public string DagVanWeek { get; set; } = string.Empty;
    public string BeschikbaarVan { get; set; } = string.Empty;
    public string BeschikbaarTot { get; set; } = string.Empty;
    public string Zonsondergang { get; set; } = string.Empty;
    public int MaxDuurMinuten { get; set; }
    public bool PastGewensteDuur { get; set; }
    public List<BestaandeWedstrijdSamenvatting> GeplandeWedstrijden { get; set; } = new();
}

public class BestaandeWedstrijdSamenvatting
{
    public string Wedstrijd { get; set; } = string.Empty;
    public string AanvangsTijd { get; set; } = string.Empty;
    public string EindTijd { get; set; } = string.Empty;
}

public class HerplanCheckRequest
{
    public long Wedstrijdcode { get; set; }
    public string? VoorkeurTijd { get; set; }
    public string? Dagdeel { get; set; }
    // "vervroegen" of "verlaten"; bepaalt of alternatieven vóór of na de huidige aanvangstijd vallen.
    public string? Richting { get; set; }
}

public class HerplanCheckResponse : IAntwoordHerplanOpties
{
    public ZoekWedstrijdResponse HuidigeWedstrijd { get; set; } = new();
    public bool Beschikbaar { get; set; }
    public List<SlotToewijzing> Alternatieven { get; set; } = new();
    public string? Reden { get; set; }
    public List<string> Waarschuwingen { get; set; } = new();
}
