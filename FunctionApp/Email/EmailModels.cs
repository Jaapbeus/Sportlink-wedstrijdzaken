namespace SportlinkFunction.Email;

// Classificatie door AI
public enum VerzoekType
{
    BeschikbaarheidCheck,
    HerplanVerzoek,
    Bevestiging,
    BuitenScope
}

public enum NamensWie
{
    Afzender,
    Tegenstander,
    Onbekend
}

// AI classificatie response
public class EmailClassificatie
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

// Inkomende email data
public class InkomendEmail
{
    public string MessageId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string Afzender { get; set; } = "";
    public string AfzenderNaam { get; set; } = "";
    public string Onderwerp { get; set; } = "";
    public DateTime OntvangstDatum { get; set; }
    public string Body { get; set; } = "";
}

// Verwerking status
public enum EmailStatus
{
    Ontvangen,
    Geclassificeerd,
    Verwerkt,
    AntwoordVerstuurd,
    Review,
    Fout,
    BuitenScope
}
