namespace BlazorAdmin.Services;

/// <summary>Weergave van feedbackstatussen en -acties in het beheeroverzicht (#764, #1478) — testbaar los van de pagina.</summary>
public static class FeedbackWeergave
{
    public static readonly (string Waarde, string Label)[] Statussen =
    [
        ("wacht-op-publicatie", "Wacht op publicatie"),
        ("gepubliceerd", "Gepubliceerd"),
        ("github-mislukt", "Publiceren mislukt"),
        ("publiceren", "Wordt gepubliceerd"),
    ];

    public static string StatusLabel(string status) =>
        Statussen.FirstOrDefault(s => s.Waarde == status).Label ?? status;

    public static string StatusBadgeKlasse(string status) => status switch
    {
        "gepubliceerd" => "bg-success",
        "wacht-op-publicatie" => "bg-warning text-dark",
        "github-mislukt" => "bg-danger",
        _ => "bg-secondary",
    };

    /// <summary>Alleen een melding die nog niet (of niet gelukt) is gepubliceerd kan met de knop gepubliceerd worden.</summary>
    public static bool KanPubliceren(string status) => status is "wacht-op-publicatie" or "github-mislukt";

    public static string MelderTekst(string? naam, bool isGeanonimiseerd) =>
        isGeanonimiseerd ? "— (geanonimiseerd)" : string.IsNullOrWhiteSpace(naam) ? "—" : naam;

    public static string ActieLabel(string actie) => actie switch
    {
        "lijst" => "Overzicht geopend",
        "detail" => "Melding bekeken",
        "publiceer" => "Melding gepubliceerd",
        _ => actie,
    };

    public static string TypeIcoon(string type) => type switch { "Fout" => "🐛", "Verzoek" => "💡", _ => "❓" };
}
