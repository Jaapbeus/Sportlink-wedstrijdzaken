namespace Planner.Shared.Feedback;

/// <summary>Status van een validate/submit-uitkomst — bepaalt welke HTTP-respons de tier-entrypoint bouwt.</summary>
public enum FeedbackStatus
{
    /// <summary>Verwerkt zonder gate-blokkade.</summary>
    Ok,

    /// <summary>Type valt buiten de vaste keuzes (#1127) — geblokkeerd vóór enige verwerking (HTTP 400).</summary>
    OngeldigType,

    /// <summary>PII gedetecteerd in invoer of AI-output (#1006) — geblokkeerd vóór AI- resp. GitHub-aanroep (HTTP 422).</summary>
    PiiGedetecteerd
}

public sealed record FeedbackValidatieResultaat(
    FeedbackStatus Status,
    string? Foutmelding,
    bool Volledig = false,
    IReadOnlyList<string>? Vragen = null);

public sealed record FeedbackSubmitResultaat(
    FeedbackStatus Status,
    string? Foutmelding,
    int IssueNummer = 0,
    string? IssueUrl = null);

/// <summary>
/// Uitkomst van de voorbeeldstap (#1205): de exacte titel en body die bij publicatie naar GitHub
/// zouden gaan, plus de losse AI-velden die de bevestigingsstap onveranderd terugstuurt. Bij een
/// geblokkeerde status blijven alle tekstvelden leeg — een geweigerd voorbeeld geeft nooit de
/// samengestelde tekst terug.
/// </summary>
public sealed record FeedbackVoorbeeldResultaat(
    FeedbackStatus Status,
    string? Foutmelding,
    string? Titel = null,
    string? Body = null,
    string? Samenvatting = null,
    IReadOnlyList<string>? Acceptatiecriteria = null);

/// <summary>
/// De door de AI geproduceerde velden zoals de beheerder ze in het voorbeeld heeft gezien (#1205).
/// Staat dit gevuld op een submit, dan publiceert de server exact die tekst in plaats van de AI
/// opnieuw aan te roepen.
/// </summary>
public sealed class FeedbackBevestiging
{
    // Nullable: deze waarden komen rechtstreeks uit de gedeserialiseerde requestbody, dus een
    // ontbrekend of null veld is een realistische invoer en geen programmeerfout.
    public string? Titel { get; set; }
    public string? Samenvatting { get; set; }
    public List<string>? Acceptatiecriteria { get; set; }
}

public sealed class FeedbackRequest
{
    public string Type { get; set; } = "";
    public string Beschrijving { get; set; } = "";
    public List<VraagAntwoord>? VragenAntwoorden { get; set; }
    public FeedbackContext? Context { get; set; }

    /// <summary>
    /// Technische context (#764): console-fouten, mislukte API-aanroepen, navigatiespoor. Standaard
    /// meegestuurd, door de gebruiker per melding uit te zetten (dan <c>null</c>). De server
    /// redigeert dit opnieuw (<see cref="FeedbackTelemetrieSaneerder"/>) vóór het wordt gebruikt.
    /// </summary>
    public FeedbackTelemetrie? Telemetrie { get; set; }

    /// <summary>
    /// Alleen gevuld op de bevestigingsstap na een voorbeeld (#1205). Zie
    /// <see cref="FeedbackCore.SubmitAsync"/> voor waarom deze clientwaarden hier veilig zijn.
    /// </summary>
    public FeedbackBevestiging? Bevestiging { get; set; }
}

public sealed class VraagAntwoord
{
    public string Vraag { get; set; } = "";
    public string Antwoord { get; set; } = "";
}

public sealed class FeedbackContext
{
    public string Pagina { get; set; } = "";
    public string Versie { get; set; } = "";
    public string Rol { get; set; } = "";
    public string Browser { get; set; } = "";
}
