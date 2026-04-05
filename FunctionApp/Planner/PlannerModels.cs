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

    // ── Optimalisatie modellen ──

    public class OptimaliseerRequest
    {
        public string Datum { get; set; } = string.Empty;
        public string? Doel { get; set; } // optioneel: veld5-ontlasten, strakker-plannen. Leeg = beide combineren
        public string? GewensteEindtijd { get; set; } // optioneel, standaard "16:15". Alles voor dit tijdstip = extra buffer
        public int? BufferMinuten { get; set; } // optioneel, standaard 15 min. Overschrijft de standaard buffer tussen wedstrijden
    }

    public class OptimaliseerResponse
    {
        public string Datum { get; set; } = string.Empty;
        public string HuidigeEindtijd { get; set; } = string.Empty;
        public string? GeschatteNieuweEindtijd { get; set; }
        public int AantalVerplaatsingen { get; set; }
        public int AantalVanVeld5Verplaatst { get; set; }
        public List<OptimalisatieSuggestie> Suggesties { get; set; } = new();
        public string HtmlPlanner { get; set; } = string.Empty;
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
}
