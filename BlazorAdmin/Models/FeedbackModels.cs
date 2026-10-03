using Planner.Shared.Feedback;

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

    /// <summary>
    /// Technische context (#764): standaard meegestuurd, door de gebruiker per melding uit te zetten
    /// (dan <c>null</c>). Al geredigeerd; de server redigeert opnieuw.
    /// </summary>
    public FeedbackTelemetrie? Telemetrie { get; set; }

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
    public Guid FeedbackId { get; set; }

    /// <summary>Kort nummer dat een gebruiker kan noemen als hij er later naar vraagt (#764).</summary>
    public string Meldingsnummer { get; set; } = "";

    public string Status { get; set; } = "";
    public bool Gepubliceerd { get; set; }

    /// <summary>Alleen gevuld voor een beheerder; een gewone gebruiker krijgt nooit een GitHub-verwijzing.</summary>
    public int IssueNummer { get; set; }
    public string? IssueUrl { get; set; }

    /// <summary>Bijv. "GitHub-integratie niet geconfigureerd" — alleen voor een beheerder.</summary>
    public string? Waarschuwing { get; set; }
}

// ── Feedbackoverzicht voor beheerders (#764, #1478) ─────────────────────────────────────────────

public class FeedbackFilterDto
{
    public string? Type { get; set; }
    public string? Status { get; set; }
    public string? Vanaf { get; set; }
    public string? Tot { get; set; }
    public string? Zoek { get; set; }
    public int Limit { get; set; } = 50;
    public int Offset { get; set; }

    public string NaarQuerystring()
    {
        var delen = new List<string>();
        void Voeg(string sleutel, string? waarde)
        {
            if (!string.IsNullOrWhiteSpace(waarde)) delen.Add($"{sleutel}={Uri.EscapeDataString(waarde.Trim())}");
        }
        Voeg("type", Type);
        Voeg("status", Status);
        Voeg("vanaf", Vanaf);
        Voeg("tot", Tot);
        Voeg("q", Zoek);
        delen.Add($"limit={Limit}");
        delen.Add($"offset={Offset}");
        return "?" + string.Join("&", delen);
    }
}

public class FeedbackItemDto
{
    public Guid FeedbackId { get; set; }
    public DateTime AangemaaktUtc { get; set; }
    public string Type { get; set; } = "";
    public string Onderwerp { get; set; } = "";
    public string? MelderNaam { get; set; }
    public bool IsGeanonimiseerd { get; set; }
    public string MelderRol { get; set; } = "";
    public string? Pagina { get; set; }
    public int? IssueNummer { get; set; }
    public string? IssueUrl { get; set; }
    public string Status { get; set; } = "";
    public string Meldingsnummer { get; set; } = "";
}

public class FeedbackLijstDto
{
    public int Totaal { get; set; }
    public int Limit { get; set; }
    public int Offset { get; set; }
    public List<FeedbackItemDto> Items { get; set; } = [];
}

public class FeedbackTelemetrieDto
{
    public string Bron { get; set; } = "";
    public string Tekst { get; set; } = "";
}

public class FeedbackVraagAntwoordDto
{
    public string Vraag { get; set; } = "";
    public string Antwoord { get; set; } = "";
}

public class FeedbackDetailDto : FeedbackItemDto
{
    public string Beschrijving { get; set; } = "";
    public List<FeedbackVraagAntwoordDto>? VragenAntwoorden { get; set; }
    public string IssueBody { get; set; } = "";
    public string? AppVersie { get; set; }
    public List<FeedbackTelemetrieDto> Telemetrie { get; set; } = [];
}

public class FeedbackPubliceerResponse
{
    public Guid FeedbackId { get; set; }
    public string Status { get; set; } = "";
    public int IssueNummer { get; set; }
    public string? IssueUrl { get; set; }
}

public class FeedbackInzageItemDto
{
    public DateTime TijdstipUtc { get; set; }
    public string? InzienDoor { get; set; }
    public string Actie { get; set; } = "";
    public Guid? FeedbackId { get; set; }
    public string? Filter { get; set; }
}

public class FeedbackInzagelogDto
{
    public int Totaal { get; set; }
    public int Limit { get; set; }
    public int Offset { get; set; }
    public List<FeedbackInzageItemDto> Items { get; set; } = [];
}
