using Planner.Shared;
using Planner.Shared.Deel;
using Planner.Shared.Email;

// Gedeelde bron voor de planner-wire-DTO's (#1856): één bestand, door beide databasetiers
// link-gecompileerd (zie docs/ARCHITECTUUR-CODEKWALITEIT.md, regel 2b). Wijzig je iets hier, dan
// wijzigt het op SQL Server én Postgres tegelijk; de naamruimte per tier blijft die van de tier.
// Endpoints: bevestig, zoek-wedstrijd en herplan-bevestig.
#if PLANNER_DTO_POSTGRES
namespace FunctionApp.Postgres.Planner;
#else
namespace SportlinkFunction.Planner;
#endif

public class BevestigRequest : global::Planner.Shared.Autorisatie.IBevestigAanvragerRequest
{
    public string Datum { get; set; } = string.Empty;
    public string AanvangsTijd { get; set; } = string.Empty;
    public int VeldNummer { get; set; }
    public string? LeeftijdsCategorie { get; set; }
    public string? TeamNaam { get; set; }
    public string? Tegenstander { get; set; }
    public string? AangevraagdDoor { get; set; }
    public int? WedstrijdDuurMinuten { get; set; }
    // Heel veld gevraagd — overschrijft de veldafmeting uit Speeltijden
    public bool? HeelVeld { get; set; }
}

// ── Herplan (herplannen) modellen ──

public class ZoekWedstrijdRequest
{
    public string TeamNaam { get; set; } = string.Empty;
    public string Datum { get; set; } = string.Empty;
}

public class ZoekWedstrijdResponse : IAntwoordWedstrijd
{
    public long Wedstrijdcode { get; set; }
    public string Wedstrijd { get; set; } = string.Empty;
    public string Datum { get; set; } = string.Empty;
    public string AanvangsTijd { get; set; } = string.Empty;
    public string EindTijd { get; set; } = string.Empty;
    public string? VeldNaam { get; set; }
    public string? LeeftijdsCategorie { get; set; }
    public int DuurMinuten { get; set; }
    public decimal VeldDeelGebruik { get; set; }
}

public class HerplanBevestigRequest : global::Planner.Shared.Autorisatie.IHerplanAanvragerRequest
{
    public long Wedstrijdcode { get; set; }
    public string GewensteAanvangsTijd { get; set; } = string.Empty;
    public int? GewenstVeldNummer { get; set; }
    public string? AangevraagdDoor { get; set; }
    public string? Opmerking { get; set; }
}

public class HerplanBevestigResponse
{
    public int Id { get; set; }
    public long Wedstrijdcode { get; set; }
    public string HuidigeWedstrijd { get; set; } = string.Empty;
    public string GewensteAanvangsTijd { get; set; } = string.Empty;
    public int? GewenstVeldNummer { get; set; }
    public string Status { get; set; } = string.Empty;
}
