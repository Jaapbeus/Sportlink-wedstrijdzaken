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
    }
}
