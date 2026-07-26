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

        /// <summary>
        /// Standaard voorkeurstijd voor deze leeftijdscategorie (#666). Wordt door de planner gebruikt
        /// als een team géén eigen rij in dbo.TeamVoorkeurTijden heeft voor de speeldag.
        /// null = geen streeftijd; de planner valt dan terug op het eerst beschikbare slot.
        /// </summary>
        public TimeOnly? StandaardVoorkeurTijd { get; set; }
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
        // Sportlink-wedstrijdcode — exacte sleutel voor herplan-exclusie (#574).
        // Null voor planner-slots die nog geen Sportlink-tegenhanger hebben.
        public long? Wedstrijdcode { get; set; }
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

    /// <summary>
    /// Uitgelezen 'VoorkeurVeld'-regel van één team (#666) — het veld waarop dit team bij voorkeur
    /// speelt, optioneel met een tijdstip. Prioriteit: laag getal = belangrijker.
    /// </summary>
    public class TeamVoorkeurVeld
    {
        public string TeamNaam { get; set; } = string.Empty;
        public int VeldNummer { get; set; }
        public TimeOnly? Tijd { get; set; }
        public int Prioriteit { get; set; }
    }

    // ── Optimalisatie modellen ──
    // OptimaliseerRequest/Response en VeldCapaciteitInfo zijn vervallen bij #666, samen met het
    // endpoint /planner/optimaliseer. OptimalisatieSuggestie blijft: PlannerHtmlGenerator gebruikt
    // het type om verplaatsingen in de HTML-weergave te markeren.

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

    // ── Veldbezetting: lichtgewicht "wat staat er nu gepland"-weergave (#566) ──
    // Bewust zonder FieldScheduler-berekening — puur een projectie van WedstrijdRaw.
    public class VeldbezettingItem
    {
        public long? WedstrijdCode { get; set; }
        public string Wedstrijd { get; set; } = string.Empty;
        public string TeamNaam { get; set; } = string.Empty;
        public string? Uitteam { get; set; }
        public string? AanvangsTijd { get; set; }
        public string? Veld { get; set; }
        public string? Competitiesoort { get; set; }
        public string? LeeftijdsCategorie { get; set; }
        public int DuurMinuten { get; set; }
        public decimal Veldafmeting { get; set; }
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
