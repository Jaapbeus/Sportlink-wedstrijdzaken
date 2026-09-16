namespace BlazorAdmin.Models;

public class FeedbackContext
{
    public string Pagina { get; set; } = "";
    public string Versie { get; set; } = "";
    public string Rol { get; set; } = "";
    public string Browser { get; set; } = "";
}

public class FeedbackVraagAntwoord
{
    public string Vraag { get; set; } = "";
    public string Antwoord { get; set; } = "";
}

/// <summary>
/// De door de AI geproduceerde velden zoals ze in het voorbeeldscherm zijn getoond (#1205). Wordt
/// alleen meegestuurd bij de publicatiestap, zodat de server exact díe tekst publiceert in plaats
/// van de AI opnieuw te bevragen — een tweede AI-aanroep zou ander resultaat geven en het getoonde
/// voorbeeld daarmee onwaar maken.
/// </summary>
public class FeedbackBevestiging
{
    public string Titel { get; set; } = "";
    public string Samenvatting { get; set; } = "";
    public List<string> Acceptatiecriteria { get; set; } = [];
}

public class FeedbackValidateRequest
{
    public string Type { get; set; } = "";
    public string Beschrijving { get; set; } = "";
    public List<FeedbackVraagAntwoord> VragenAntwoorden { get; set; } = [];
    public FeedbackContext? Context { get; set; }

    /// <summary>Alleen gevuld bij de publicatiestap na een voorbeeld (#1205).</summary>
    public FeedbackBevestiging? Bevestiging { get; set; }
}

/// <summary>
/// De exacte titel en body die naar GitHub zouden gaan (#1205) — wat de beheerder te zien krijgt
/// vóór hij bewust bevestigt dat dit openbaar gepubliceerd mag worden.
/// </summary>
public class FeedbackPreviewResponse
{
    public string Titel { get; set; } = "";
    public string Body { get; set; } = "";
    public string Samenvatting { get; set; } = "";
    public List<string> Acceptatiecriteria { get; set; } = [];
}

public class FeedbackValidateResponse
{
    public bool Volledig { get; set; }
    public List<string> Vragen { get; set; } = [];
}

public class FeedbackSubmitResponse
{
    public int IssueNummer { get; set; }
    public string IssueUrl { get; set; } = "";
}
