namespace Planner.Shared.Email;

// Gedeelde domeintypen van de e-mailverwerking (#1716 slice 5). Stonden tot en met slice 4
// identiek per tier (FunctionApp/Email/BerichtModels.cs en FunctionApp.Postgres/Email/BerichtModels.cs).
// Contract dat niet mag schuiven zonder datamigratie: de classificatie gaat via
// JsonConvert.SerializeObject (Newtonsoft, enums als getal) naar planner.EmailVerwerking en wordt daar
// ook weer uitgelezen. Volgorde en getalswaarden van de enums en de propertynamen liggen daarom vast;
// BerichtDomeinTypenTests in Planner.Shared.Tests bewaakt ze.

/// <summary>Soort verzoek, door de AI-classificatie bepaald.</summary>
public enum VerzoekType
{
    BeschikbaarheidCheck,
    HerplanVerzoek,
    Bevestiging,
    TeamContactOpvragen,  // #168: "wie is de trainer/coach van [team]?"
    BuitenScope
}

/// <summary>Namens wie de afzender schrijft.</summary>
public enum NamensWie
{
    Afzender,
    Tegenstander,
    Onbekend
}

/// <summary>AI-classificatie van een inkomend bericht, aangevuld door de pipeline.</summary>
public class BerichtClassificatie
{
    public VerzoekType Type { get; set; }
    public string? Datum { get; set; }           // yyyy-MM-dd — eerste/primaire datum
    public string? AanvangsTijd { get; set; }    // HH:mm
    // ochtend | middag | avond — alleen als de afzender een dagdeel noemt (#1587); nooit afgeleid uit een tijd
    public string? Dagdeel { get; set; }
    public string? GewensteDatum { get; set; }   // yyyy-MM-dd — gewenste nieuwe datum (bij herplan)
    public List<string>? Datums { get; set; }    // Meerdere datums bij multi-datum verzoek
    public string? TeamNaam { get; set; }
    public string? LeeftijdsCategorie { get; set; }
    public string? Tegenstander { get; set; }
    public string Samenvatting { get; set; } = "";
    public NamensWie NamensWie { get; set; }
    // Gevuld door AI als het verzoek mogelijk een KNVB-regel overtreedt (#73)
    public string? KnvbNotitie { get; set; }
    // Heel veld gevraagd — overschrijft de standaard veldafmeting uit Speeltijden
    public bool? HeelVeld { get; set; }

    // Door de pipeline gezet (niet door AI) als de teamresolutie TeamNaam niet kon bevestigen: antwoorden
    // gebruiken dan generieke tekst in plaats van de vrije modeltekst.
    public bool TeamNaamOnbevestigd { get; set; }

    // Door de pipeline gezet (niet door AI) wanneer een KNVB-bijlage + BCC van toepassing is (#561/#1141).
    // Houd dit klein (strings, geen bytes): de classificatie wordt naar planner.EmailVerwerking gelogd —
    // grote binaire payloads horen daar niet in.
    public bool VoegKnvbPdfBijlageToe { get; set; }
    public string? KnvbBijlageRegio { get; set; }

    /// <summary>
    /// Retourneert alle unieke datums: Datums als die er zijn, anders alleen Datum.
    /// </summary>
    public List<string> GetAlleDatums()
    {
        if (Datums != null && Datums.Count > 0)
            return Datums.Distinct().ToList();
        if (!string.IsNullOrEmpty(Datum))
            return new List<string> { Datum };
        return new List<string>();
    }
}
