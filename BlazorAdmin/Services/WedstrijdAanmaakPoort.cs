namespace BlazorAdmin.Services;

/// <summary>
/// Poort vóór het aanmaken van een wedstrijd in Sportlink (#1436). Bij een acceptatietest werd een
/// wedstrijd aangemaakt zonder dat iemand op de knop klikte: het formulier was een
/// <c>&lt;form @onsubmit&gt;</c>, dus elke Enter in een tekstveld was een aanmaak — met dry-run uit
/// rechtstreeks in Sportlink. Deze klasse legt de drie regels vast die dat voorkomen, los van de
/// Razor-pagina zodat ze testbaar zijn:
/// <list type="number">
/// <item>Validatie met eigen Nederlandse meldingen (geen browservalidatie, die is Engelstalig).</item>
/// <item>Zolang dry-run niet aantoonbaar aan staat, is een expliciete bevestiging nodig.</item>
/// <item>Er loopt hooguit één aanvraag tegelijk.</item>
/// </list>
/// </summary>
public sealed class WedstrijdAanmaakPoort
{
    public enum Stap { Invoer, Bevestigen, Bezig }

    public Stap Huidig { get; private set; } = Stap.Invoer;

    /// <summary>Invoervelden zijn alleen bewerkbaar in de invoerstap — wat bevestigd wordt, is wat verstuurd wordt.</summary>
    public bool InvoerVergrendeld => Huidig != Stap.Invoer;

    /// <summary>
    /// Klik op "Wedstrijd aanmaken". Geeft <c>true</c> als er meteen verstuurd mag worden (alleen bij
    /// dry-run aan); anders gaat de poort naar <see cref="Stap.Bevestigen"/> of blijft hij in de
    /// invoerstap als er validatiefouten zijn. Een onbekende dry-run-stand (<c>null</c>) telt als
    /// "uit": bij twijfel wordt er bevestigd.
    /// </summary>
    public bool VraagAan(IReadOnlyList<string> validatieFouten, bool? dryRun)
    {
        if (Huidig != Stap.Invoer || validatieFouten.Count > 0) return false;
        if (dryRun == true) { Huidig = Stap.Bezig; return true; }
        Huidig = Stap.Bevestigen;
        return false;
    }

    /// <summary>Klik op "Bevestigen". Alleen vanuit de bevestigstap; een tweede klik doet niets.</summary>
    public bool Bevestig()
    {
        if (Huidig != Stap.Bevestigen) return false;
        Huidig = Stap.Bezig;
        return true;
    }

    /// <summary>Klik op "Annuleren" in de bevestigstap.</summary>
    public void Annuleer()
    {
        if (Huidig == Stap.Bevestigen) Huidig = Stap.Invoer;
    }

    /// <summary>De aanvraag is afgerond (geslaagd of niet) — terug naar invoer.</summary>
    public void Klaar() => Huidig = Stap.Invoer;

    /// <summary>Verplichte velden en hun Nederlandse melding. Lege lijst = bruikbaar.</summary>
    public static IReadOnlyList<string> Valideer(DateTime? datum, string? tijd, int? duur, string? teamNaam, string? tegenstander, string? velddeel = null)
    {
        var fouten = new List<string>();
        if (datum == null) fouten.Add("Vul een datum in.");
        if (string.IsNullOrWhiteSpace(tijd) || !TimeSpan.TryParse(tijd, out _)) fouten.Add("Vul een geldige aanvangstijd in (bijv. 19:00).");
        if (duur is null or < 1 or > 240) fouten.Add("Vul een duur in tussen 1 en 240 minuten.");
        if (string.IsNullOrWhiteSpace(teamNaam)) fouten.Add("Kies een team of vul een vrije teamnaam in.");
        if (string.IsNullOrWhiteSpace(tegenstander)) fouten.Add("Vul een tegenstander in.");
        if (!string.IsNullOrWhiteSpace(velddeel) && !OefenwedstrijdFormulierState.Velddelen.Any(v => v.Waarde == velddeel))
            fouten.Add("Kies een velddeel uit de lijst (heel, half, kwart of achtste veld).");
        return fouten;
    }
}
