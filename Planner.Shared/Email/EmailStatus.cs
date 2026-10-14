namespace Planner.Shared.Email;

/// <summary>
/// Verwerkingsstatus van een inkomend bericht (#1716 slice 5: eenmalig hier, stond identiek per tier).
/// De naam (<c>ToString()</c>) gaat als statuskolom naar planner.EmailVerwerking en wordt daar met
/// <c>Enum.TryParse</c> teruggelezen; namen en volgorde liggen vast (BerichtDomeinTypenTests).
/// </summary>
public enum EmailStatus
{
    Ontvangen,
    Geclassificeerd,
    Verwerkt,
    AntwoordVerstuurd,
    Review,
    Fout,
    BuitenScope,
    // Verwerkt, maar bewust geen automatisch antwoord verstuurd omdat planning mogelijk is —
    // de coördinator plant handmatig in en koppelt zelf terug (#572).
    GeenAntwoordNodig
}
