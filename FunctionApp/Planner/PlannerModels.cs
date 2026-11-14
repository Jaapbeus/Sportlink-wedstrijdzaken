using System;
using System.Collections.Generic;
using Planner.Shared;
using Planner.Shared.Deel;
using Planner.Shared.Email;

namespace SportlinkFunction.Planner
{

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
        /// <summary>Netto speelduur volgens Sportlink (his.matchdetails.Duration), indien bekend (#1547).</summary>
        public int? SportlinkSpeelduur { get; set; }
        /// <summary>Het bondswedstrijdnummer (his.matches.wedstrijdnummer); koppelsleutel naar de veldplanner (#1915).</summary>
        public long? Wedstrijdnummer { get; set; }
        /// <summary>Sportlinks PublicMatchId uit de geverifieerde cache, indien bekend; koppelsleutel naar de veldplanner (#1915).</summary>
        public string? PublicMatchId { get; set; }
    }


    // ── Veldbezetting: lichtgewicht "wat staat er nu gepland"-weergave (#566) ──
    // Bewust zonder FieldScheduler-berekening — puur een projectie van WedstrijdRaw.
    // IVeldbezettingRegel (#1363): de gedeelde PDF-export leest deze regel zonder eigen mapping per tier.
    public class VeldbezettingItem : IVeldbezettingRegel
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
        /// <summary>De andere kant van <see cref="Wedstrijd"/> ten opzichte van <see cref="TeamNaam"/> (#1582).</summary>
        public string? Tegenstander { get; set; }
        /// <summary>Onze eigen regel die de Sportlink-veldplanner niet kent (#1582): een afwijking, zichtbaar gemaakt.</summary>
        public bool NietInSportlink { get; set; }
        /// <summary>"Sportlink" als veld, tijd en duur uit de veldplanner komen (#1582).</summary>
        public string? Bron { get; set; }
        /// <summary>Koppelsleutel naar de Sportlink-veldplanner (#1915). Alleen intern: niet in het API-antwoord.</summary>
        [System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore]
        public long? Wedstrijdnummer { get; set; }
        /// <summary>Koppelsleutel naar de Sportlink-veldplanner (#1915). Alleen intern: niet in het API-antwoord.</summary>
        [System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore]
        public string? PublicMatchId { get; set; }
    }

    // #1856: de overige auto-plan-DTO's staan in GedeeldeBron/PlannerDto/AutoPlanModels.cs. Deze twee
    // typen blijven tijdelijk per tier, omdat open PR's (#1823, #1828, #1843) ze wijzigen; daarna
    // volgen ze naar de gedeelde bron (zie docs/ARCHITECTUUR-CODEKWALITEIT.md, regel 2b).
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
        public bool SportlinkLeidend { get; set; }
    }

    public class AutoPlanToepassenRequest
    {
        public string Datum { get; set; } = string.Empty;
        public int? BufferMinuten { get; set; }
        public List<global::Planner.Shared.AutoPlanToewijzing>? Toewijzingen { get; set; }
    }
}
