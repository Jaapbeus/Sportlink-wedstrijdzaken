using System;
using System.Collections.Generic;

namespace SportlinkFunction.Planner
{
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

    public class BevestigRequest
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

    // ── Antwoord ──

    public class CheckAvailabilityResponse
    {
        public bool Beschikbaar { get; set; }
        public SlotToewijzing? Toewijzing { get; set; }
        public TeamConflictInfo? TeamConflict { get; set; }
        public string? Reden { get; set; }
        public List<SlotToewijzing> Alternatieven { get; set; } = new();
        public List<BeschikbaarVenster>? BeschikbareVensters { get; set; }
        public List<string> Waarschuwingen { get; set; } = new();
    }

    public class SlotToewijzing
    {
        public string Datum { get; set; } = string.Empty;
        public string AanvangsTijd { get; set; } = string.Empty;
        public string EindTijd { get; set; } = string.Empty;
        public int VeldNummer { get; set; }
        public string VeldNaam { get; set; } = string.Empty;
        public decimal VeldDeelGebruik { get; set; }
        public int WedstrijdDuurMinuten { get; set; }
    }

    public class TeamConflictInfo
    {
        public string Wedstrijd { get; set; } = string.Empty;
        public string AanvangsTijd { get; set; } = string.Empty;
        public string EindTijd { get; set; } = string.Empty;
        public string VeldNaam { get; set; } = string.Empty;
    }

    public class BeschikbaarVenster
    {
        public int VeldNummer { get; set; }
        public string VeldNaam { get; set; } = string.Empty;
        public string Van { get; set; } = string.Empty;
        public string Tot { get; set; } = string.Empty;
        public int MaxDuurMinuten { get; set; }
        public string? Opmerking { get; set; }
    }

    // ── Interne modellen ──

    public class Speeltijd
    {
        public string Leeftijd { get; set; } = string.Empty;
        public decimal Veldafmeting { get; set; }
        public int WedstrijdTotaal { get; set; }
    }

    public class VeldInfo
    {
        public int VeldNummer { get; set; }
        public string VeldNaam { get; set; } = string.Empty;
        public string VeldType { get; set; } = "kunstgras"; // kunstgras, natuurgras
        public bool HeeftKunstlicht { get; set; }
        public bool IsKunstgras => VeldType == "kunstgras";
    }

    public class VeldBeschikbaarheidInfo
    {
        public int VeldNummer { get; set; }
        public TimeOnly BeschikbaarVanaf { get; set; }
        public TimeOnly BeschikbaarTot { get; set; }
        public bool GebruikZonsondergang { get; set; }
    }

    public class BestaandeWedstrijd
    {
        public DateOnly Datum { get; set; }
        public TimeOnly AanvangsTijd { get; set; }
        public TimeOnly EindTijd { get; set; }
        public int VeldNummer { get; set; }
        public decimal VeldDeelGebruik { get; set; }
        public string? VeldSubpositie { get; set; } // A, B, A1, A2, B1, B2 — voor visuele positionering
        public string? LeeftijdsCategorie { get; set; }
        public string? TeamNaam { get; set; }
        public string? Wedstrijd { get; set; }
        public string Bron { get; set; } = string.Empty;
    }

    public class TeamRegel
    {
        public string TeamNaam { get; set; } = string.Empty;
        public string RegelType { get; set; } = string.Empty;
        public int? WaardeMinuten { get; set; }
        public int? WaardeVeldNummer { get; set; }
        public TimeOnly? WaardeTijd { get; set; }
        public int Prioriteit { get; set; }
    }

    // ── Optimalisatie modellen ──

    public class OptimaliseerRequest
    {
        public string Datum { get; set; } = string.Empty;
        public string? Doel { get; set; } // optioneel: grasveld-ontlasten, strakker-plannen. Leeg = beide combineren
        public string? GewensteEindtijd { get; set; } // optioneel, standaard "16:15". Alles voor dit tijdstip = extra buffer
        public int? BufferMinuten { get; set; } // optioneel, standaard 15 min. Overschrijft de standaard buffer tussen wedstrijden
    }

    public class OptimaliseerResponse
    {
        public string Datum { get; set; } = string.Empty;
        public string HuidigeEindtijd { get; set; } = string.Empty;
        public string? GeschatteNieuweEindtijd { get; set; }
        public int AantalVerplaatsingen { get; set; }
        public int AantalVanGrasveldVerplaatst { get; set; }
        public List<OptimalisatieSuggestie> Suggesties { get; set; } = new();
        public string HtmlPlanner { get; set; } = string.Empty;
        public bool VoldoendeRuimte { get; set; }
        public string? VoldoendeRuimteMelding { get; set; }
        public VeldCapaciteitInfo? CapaciteitOverzicht { get; set; }
    }

    public class VeldCapaciteitInfo
    {
        public int TotaalBeschikbareMinuten { get; set; }
        public int TotaalBezettMinuten { get; set; }
        public double BezettingsPercentage { get; set; }
        public int AantalWedstrijdenOpGrasveld { get; set; }
        public int AantalLegeVelden { get; set; }
    }

    public class OptimalisatieSuggestie
    {
        public string Wedstrijd { get; set; } = string.Empty;
        public int HuidigVeldNummer { get; set; }
        public string HuidigVeld { get; set; } = string.Empty;
        public string HuidigeTijd { get; set; } = string.Empty;
        public int NieuwVeldNummer { get; set; }
        public string NieuwVeld { get; set; } = string.Empty;
        public string NieuweTijd { get; set; } = string.Empty;
        public string Reden { get; set; } = string.Empty;
    }

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

    // ── Herplan (herplannen) modellen ──

    public class ZoekWedstrijdRequest
    {
        public string TeamNaam { get; set; } = string.Empty;
        public string Datum { get; set; } = string.Empty;
    }

    public class ZoekWedstrijdResponse
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

    public class HerplanCheckRequest
    {
        public long Wedstrijdcode { get; set; }
        public string? VoorkeurTijd { get; set; }
        public string? Dagdeel { get; set; }
        // "vervroegen" of "verlaten"; bepaalt of alternatieven vóór of na de huidige aanvangstijd vallen.
        public string? Richting { get; set; }
    }

    public class HerplanCheckResponse
    {
        public ZoekWedstrijdResponse HuidigeWedstrijd { get; set; } = new();
        public bool Beschikbaar { get; set; }
        public List<SlotToewijzing> Alternatieven { get; set; } = new();
        public string? Reden { get; set; }
        public List<string> Waarschuwingen { get; set; } = new();
    }

    public class HerplanBevestigRequest
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

    // ── Team schedule modellen (#70) ──

    public class TeamScheduleWedstrijd
    {
        public string Datum { get; set; } = string.Empty;
        public string AanvangsTijd { get; set; } = string.Empty;
        public string ThuisUit { get; set; } = string.Empty;  // "thuis" | "uit"
        public string Tegenstander { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;      // "competitie" | "beker" | "oefenwedstrijd"
        public string? Veld { get; set; }
        public long? Wedstrijdcode { get; set; }
    }

    public class TeamScheduleZaterdag
    {
        public string Datum { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;    // "vrij" | "oefenwedstrijd" | "bezet"
        public TeamScheduleWedstrijd? BezetDoor { get; set; }
    }

    public class TeamScheduleResponse
    {
        public string Team { get; set; } = string.Empty;
        public string SeizoenEinde { get; set; } = string.Empty;
        public List<TeamScheduleZaterdag> Zaterdagen { get; set; } = new();
        public List<TeamScheduleWedstrijd> Wedstrijden { get; set; } = new();
    }

    // ── AVG: TeamleiderContact bevat persoonsgegevens — gebruik alleen voor interne notificaties ──
    public class TeamleiderContact
    {
        public string Naam { get; set; } = string.Empty;
        public string Emailadres { get; set; } = string.Empty;
    }

    // ── Auto-plan modellen (#380) ──

    // Ruwe wedstrijddata voor auto-plan (inclusief wedstrijden zonder veld/aanvangstijd)
    public class WedstrijdRaw
    {
        public long? WedstrijdCode { get; set; }
        public string Wedstrijd { get; set; } = string.Empty;
        public string TeamNaam { get; set; } = string.Empty;
        public string? Uitteam { get; set; }
        public string? AanvangsTijd { get; set; }
        public string? Veld { get; set; }
        public string? LeeftijdsCategorie { get; set; }
        public string? Competitiesoort { get; set; }
    }

    public class AutoPlanRequest
    {
        public string Datum { get; set; } = string.Empty;
        public int? BufferMinuten { get; set; }
    }

    public class AutoPlanWedstrijdItem
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
        public string Status { get; set; } = "ongewijzigd";
        public string? NietInplanbaaarReden { get; set; }

        // Voorkeurstijd-informatie (null = geen voorkeur geconfigureerd)
        public string? VoorkeurTijd { get; set; }
        public int? VoorkeurAfwijkingMinuten { get; set; }  // 0 = exact, positief = later, negatief = eerder
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
}
