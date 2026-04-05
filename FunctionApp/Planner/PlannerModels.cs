using System;
using System.Collections.Generic;

namespace SportlinkFunction.Planner
{
    // ── Request ──

    public class CheckAvailabilityRequest
    {
        public string Datum { get; set; } = string.Empty;
        public string? AanvangsTijd { get; set; }
        public string? Dagdeel { get; set; } // "ochtend", "middag", "avond"
        public string? LeeftijdsCategorie { get; set; }
        public string? TeamNaam { get; set; }
        public string? Tegenstander { get; set; }
        public int? WedstrijdDuurMinuten { get; set; }
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
    }

    // ── Response ──

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

    // ── Internal models ──

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
        public bool HeeftKunstlicht { get; set; }
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
}
