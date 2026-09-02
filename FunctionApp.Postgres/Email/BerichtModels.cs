namespace FunctionApp.Postgres.Email;

// Postgres-tier-tegenhanger van de classificatie-typen uit FunctionApp/Email/BerichtModels.cs
// (#889). InkomendBericht/EmailStatus/ClassificatieCorrectieVoorbeeld staan NIET hier — die
// bestonden al in EmailModels.cs (#889, eerdere sessie) vóórdat deze vertaling begon.

// Classificatie door AI
public enum VerzoekType
{
    BeschikbaarheidCheck,
    HerplanVerzoek,
    Bevestiging,
    TeamContactOpvragen,  // #168: "wie is de trainer/coach van [team]?"
    BuitenScope
}

public enum NamensWie
{
    Afzender,
    Tegenstander,
    Onbekend
}

// AI classificatie response
public class BerichtClassificatie
{
    public VerzoekType Type { get; set; }
    public string? Datum { get; set; }           // yyyy-MM-dd — eerste/primaire datum
    public string? AanvangsTijd { get; set; }    // HH:mm
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

    // Door de pipeline gezet (niet door AI) wanneer een KNVB-bijlage + BCC van toepassing is (#561).
    // Niet vertaald op deze tier — zie BerichtPipeline: het "verzet zonder datum"-pad valt hier
    // altijd terug op het standaard herplan-pad (geen knvbStandaardRegio-instelling op deze tier).
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
