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

public class FeedbackValidateRequest
{
    public string Type { get; set; } = "";
    public string Beschrijving { get; set; } = "";
    public List<FeedbackVraagAntwoord> VragenAntwoorden { get; set; } = [];
    public FeedbackContext? Context { get; set; }
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
