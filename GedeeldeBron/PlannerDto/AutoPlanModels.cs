using Planner.Shared;
using Planner.Shared.Deel;
using Planner.Shared.Email;

// Gedeelde bron voor de planner-wire-DTO's (#1856): één bestand, door beide databasetiers
// link-gecompileerd (zie docs/ARCHITECTUUR-CODEKWALITEIT.md, regel 2b). Wijzig je iets hier, dan
// wijzigt het op SQL Server én Postgres tegelijk; de naamruimte per tier blijft die van de tier.
// Endpoints: auto-plan en auto-plan/toepassen.
#if PLANNER_DTO_POSTGRES
namespace FunctionApp.Postgres.Planner;
#else
namespace SportlinkFunction.Planner;
#endif

public class AutoPlanRequest
{
    public string Datum { get; set; } = string.Empty;
    public int? BufferMinuten { get; set; }
}

// IPlanWedstrijdRegel (#1363): zie VeldbezettingItem hierboven.

public class AutoPlanWedstrijdItem : IPlanWedstrijdRegel
{
    public long? WedstrijdCode { get; set; }
    public string Wedstrijd { get; set; } = string.Empty;
    public string TeamNaam { get; set; } = string.Empty;
    public string? LeeftijdsCategorie { get; set; }
    public string? Competitiesoort { get; set; }
    public int DuurMinuten { get; set; }
    public decimal Veldafmeting { get; set; }

    // Huidige situatie
    public string? HuidigeVeld { get; set; }
    public string? HuidigeTijd { get; set; }
    public bool HeeftVeld { get; set; }
    public bool HeeftTijd { get; set; }

    // Optimale situatie
    public int? OptimaalVeldNummer { get; set; }
    public string? OptimaalVeldNaam { get; set; }
    public string? OptimaalVeld { get; set; }  // Sportlink-formaat "veld 3 A"
    public string? OptimaalTijd { get; set; }  // "09:00"

    // Status: "nieuw-slot" | "wijziging" | "ongewijzigd" | "niet-inplanbaar"
    // Let op: dit zegt alleen of de planner de wedstrijd verplaatst t.o.v. de HUIDIGE stand.
    // Of de wedstrijd op de gewenste voorkeurstijd staat, staat in VoorkeurStatus (#666) — die twee
    // werden eerder door elkaar gehaald, waardoor een wedstrijd met 60 min afwijking "OK" toonde.
    public string Status { get; set; } = "ongewijzigd";
    public string? NietInplanbaaarReden { get; set; }

    // Voorkeurstijd-informatie (null = geen voorkeur en geen default geconfigureerd)
    public string? VoorkeurTijd { get; set; }
    public int? VoorkeurAfwijkingMinuten { get; set; }  // 0 = exact, positief = later, negatief = eerder

    /// <summary>
    /// Waar de voorkeurstijd uit komt (#666): "regel" (dbo.TeamRegels VoorkeurVeld met tijd),
    /// "team" (dbo.TeamVoorkeurTijden) of "leeftijd" (dbo.Speeltijden.StandaardVoorkeurTijd).
    /// null = geen voorkeurstijd bekend.
    /// </summary>
    public string? VoorkeurBron { get; set; }

    /// <summary>
    /// Beoordeling van de afwijking t.o.v. de voorkeurstijd (#666), met dezelfde drempels als de
    /// Gantt-legenda: "op-tijd" (exact), "kleine-afwijking" (t/m 15 min), "grote-afwijking"
    /// (meer dan 15 min), "geen-voorkeur".
    /// </summary>
    public string VoorkeurStatus { get; set; } = "geen-voorkeur";

    /// <summary>Voorkeursveld uit een 'VoorkeurVeld'-teamregel; null als die regel er niet is.</summary>
    public int? VoorkeurVeldNummer { get; set; }

    /// <summary>False als er een voorkeursveld was maar de planner een ander veld moest kiezen.</summary>
    public bool? VoorkeurVeldToegepast { get; set; }

    /// <summary>
    /// Actieve teamregel <c>BufferVoor</c> in minuten, of null zonder regel (#1430). Ruwe waarde, niet
    /// de effectieve: de client neemt het maximum met de algemene buffer (PlanningBufferRegels).
    /// </summary>
    public int? TeamBufferVoor { get; set; }

    /// <summary>Actieve teamregel <c>BufferNa</c> in minuten, of null zonder regel (#1430).</summary>
    public int? TeamBufferNa { get; set; }
}

public class AutoPlanResponse
{
    public string Datum { get; set; } = string.Empty;
    public int TotaalWedstrijden { get; set; }
    public int ZonderVeld { get; set; }
    public int ZonderTijd { get; set; }
    public int TeWijzigen { get; set; }
    public int NietInplanbaar { get; set; }
    public string? GeschatteEindTijd { get; set; }
    public List<AutoPlanWedstrijdItem> Wedstrijden { get; set; } = new();
    public string HuidigeHtml { get; set; } = string.Empty;
    public string OptimaleHtml { get; set; } = string.Empty;
}

public class AutoPlanToepassenRequest
{
    public string Datum { get; set; } = string.Empty;
    public int? BufferMinuten { get; set; }
}

public class AutoPlanToepassenResponse
{
    public int Bijgewerkt { get; set; }
    public int Mislukt { get; set; }
    public List<string> Fouten { get; set; } = new();
}
