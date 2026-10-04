using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Teksten en keuzes van de feedbackwidget, los van de component zodat ze testbaar zijn (#764).
/// De widget is voor elke ingelogde rol; wat een melder te lezen krijgt hangt af van wat er met zijn
/// melding gebeurt: een beheerder publiceert zelf, een gewone gebruiker niet.
/// </summary>
public static class FeedbackWidgetState
{
    /// <summary>De waarden zijn de vaste server-allowlist (FeedbackCore.ToegestaneTypes); alleen het label is gebruikerstaal.</summary>
    public static readonly (string Waarde, string Label)[] Types =
    [
        ("Fout", "Er gaat iets mis"),
        ("Verzoek", "Ik mis iets"),
        ("Vraag", "Ik snap iets niet"),
    ];

    public static string LabelVoor(string type) =>
        Types.FirstOrDefault(t => t.Waarde == type).Label ?? type;

    public static string PrivacyUitleg(bool isAdmin) => isAdmin
        ? "Je naam wordt bewaard in het beheeroverzicht. De tekst komt zonder je naam op een openbare pagina — zet er geen namen, e-mailadressen of telefoonnummers in."
        : "Je naam wordt bewaard voor de beheerder. De tekst komt zonder je naam op een openbare pagina — zet er geen namen, e-mailadressen of telefoonnummers in.";

    public static string PubliciteitWaarschuwing(bool isAdmin) => isAdmin
        ? "⚠️ Deze melding wordt openbaar op internet gepubliceerd als GitHub-issue — iedereen kan hem lezen. Voeg geen persoonsgegevens toe (namen, adressen, geboortedata, e-mailadressen, telefoonnummers) en geen wachtwoorden of sleutels. In de volgende stap ziet u de volledige tekst en bevestigt u zelf dat die openbaar mag."
        : "Je melding komt, nadat een beheerder hem heeft bekeken, op de openbare takenlijst van de ontwikkelaar. Voeg daarom geen persoonsgegevens toe (namen, adressen, geboortedata, e-mailadressen, telefoonnummers) en geen wachtwoorden.";

    public static string BevestigingTekst(bool isAdmin, FeedbackSubmitResponse? antwoord)
    {
        if (isAdmin)
            return antwoord is { Gepubliceerd: true }
                ? "Je melding is ontvangen en staat klaar voor de ontwikkelaar."
                : "Je melding is bewaard in het feedbackoverzicht.";

        return "Je melding is opgeslagen. De ontwikkelaar leest alle meldingen, maar reageert niet persoonlijk. "
             + "Zie je hetzelfde nog een keer? Meld het gerust opnieuw — dan weten we dat het vaker gebeurt.";
    }
}
