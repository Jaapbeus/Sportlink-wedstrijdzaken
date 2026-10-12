namespace Planner.Shared.Infrastructure;

/// <summary>Stand van de lokale ontwikkelbypass (#1700).</summary>
public enum LokaleBypassStand
{
    /// <summary>Geen schakelaar: productiegedrag. Platform-auth is verplicht, ook buiten Azure.</summary>
    Uit,
    /// <summary>Schakelaar aan en de app draait niet in Azure: lokale ontwikkelomgeving.</summary>
    Actief,
    /// <summary>Schakelaar aan terwijl de app in Azure draait: geweigerd, dus productiegedrag.</summary>
    GeweigerdInAzure,
}

/// <summary>
/// De enige beslisplek voor de lokale ontwikkelbypass (#1700), tier-onafhankelijk. De tiers lezen hem via
/// een dunne adapter (<c>EasyAuthHelper</c> en <c>EgressGuard</c> per tier).
/// <para>
/// <b>Waarom.</b> Tot #1700 gold "geen <c>WEBSITE_SITE_NAME</c>" als lokale ontwikkeling, met als gevolg dat
/// elke rolcontrole openviel zodra die platformvariabele ontbrak, bijvoorbeeld op een andere hosting (#1542).
/// De afwezigheid van een variabele is geen toestemming. Nu is de bypass een expliciete keuze:
/// <c>LOCAL_AUTH_BYPASS=true</c>, en geweigerd zodra de app in Azure draait.
/// </para>
/// <para>
/// <b>Faalt veilig.</b> Zonder schakelaar, of met een andere waarde dan <c>true</c>: geen bypass. Met schakelaar
/// in Azure: geen bypass, en uitgaand verkeer blijft dicht (bij twijfel dicht, ARCHITECTUUR.md §8.2). Uitgaand
/// verkeer opent alleen in productie (Azure zonder schakelaar) of met de expliciete opt-in
/// <c>AllowExternalIntegrations=true</c> (#857); de lokale schakelaar opent het nooit.
/// </para>
/// <para>
/// Alle methodes hebben een variant met een lees-delegate, zodat de regel zonder procesomgeving te testen is.
/// Eenmalige opstartmelding (<see cref="MeldOpstart"/>), zonder waarden.
/// </para>
/// </summary>
public static class LokaleOntwikkelomgeving
{
    /// <summary>De expliciete schakelaar voor lokale ontwikkeling.</summary>
    public const string Schakelaar = "LOCAL_AUTH_BYPASS";

    /// <summary>Platformvariabele die Azure op elke gehoste Function App zet.</summary>
    public const string AzureHostingVariabele = "WEBSITE_SITE_NAME";

    /// <summary>De expliciete opt-in voor uitgaand verkeer buiten productie (#857).</summary>
    public const string ExterneIntegratiesOptIn = "AllowExternalIntegrations";

    /// <summary>Bepaalt de stand uit de opgegeven variabelen.</summary>
    public static LokaleBypassStand Bepaal(Func<string, string?> lees)
    {
        if (!IsWaar(lees(Schakelaar))) return LokaleBypassStand.Uit;
        return DraaitInAzure(lees) ? LokaleBypassStand.GeweigerdInAzure : LokaleBypassStand.Actief;
    }

    /// <summary>True alleen als de bypass expliciet is aangevraagd én de app niet in Azure draait.</summary>
    public static bool BypassActief(Func<string, string?> lees) => Bepaal(lees) == LokaleBypassStand.Actief;

    /// <inheritdoc cref="BypassActief(Func{string, string?})"/>
    public static bool BypassActief() => BypassActief(Environment.GetEnvironmentVariable);

    /// <summary>True als de platformvariabele van Azure-hosting gezet is.</summary>
    public static bool DraaitInAzure(Func<string, string?> lees) => !string.IsNullOrEmpty(lees(AzureHostingVariabele));

    /// <inheritdoc cref="DraaitInAzure(Func{string, string?})"/>
    public static bool DraaitInAzure() => DraaitInAzure(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Mogen uitgaande integraties (Sportlink, Graph, AI, GitHub) worden aangeroepen? Ja met de opt-in; anders
    /// alleen in productie: Azure-hosting zonder lokale schakelaar.
    /// </summary>
    public static bool ExterneIntegratiesToegestaan(Func<string, string?> lees)
        => IsWaar(lees(ExterneIntegratiesOptIn)) || (DraaitInAzure(lees) && !IsWaar(lees(Schakelaar)));

    /// <inheritdoc cref="ExterneIntegratiesToegestaan(Func{string, string?})"/>
    public static bool ExterneIntegratiesToegestaan() => ExterneIntegratiesToegestaan(Environment.GetEnvironmentVariable);

    /// <summary>
    /// De opstartmelding voor deze stand, zonder waarden van omgevingsvariabelen; <c>null</c> als er niets te
    /// melden is (geen schakelaar = productiegedrag).
    /// </summary>
    public static string? OpstartMelding(Func<string, string?> lees) => Bepaal(lees) switch
    {
        LokaleBypassStand.GeweigerdInAzure =>
            $"WAARSCHUWING: {Schakelaar} is gezet maar geweigerd: de app draait in Azure. Platform-authenticatie blijft " +
            "verplicht en uitgaande integraties blijven dicht tot de instelling is verwijderd (#1700).",
        LokaleBypassStand.Actief =>
            $"Lokale ontwikkelmodus: {Schakelaar} staat aan, de rolcontrole wordt overgeslagen. " +
            "Alleen bedoeld voor een ontwikkelmachine (#1700).",
        _ => null,
    };

    /// <summary>
    /// Schrijft de opstartmelding eenmalig naar <paramref name="uit"/> (de tiers: de standaarduitvoer, die de
    /// Functions-host overneemt). Zonder schakelaar wordt er niets geschreven.
    /// </summary>
    public static void MeldOpstart(TextWriter uit) => MeldOpstart(uit, Environment.GetEnvironmentVariable);

    /// <inheritdoc cref="MeldOpstart(TextWriter)"/>
    public static void MeldOpstart(TextWriter uit, Func<string, string?> lees)
    {
        if (OpstartMelding(lees) is { } melding) uit.WriteLine(melding);
    }

    private static bool IsWaar(string? waarde) => string.Equals(waarde?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
