namespace BlazorAdmin.Services;

/// <summary>
/// Onthoudt voor welke club een pagina zijn gegevens heeft geladen (#1578), zodat een
/// herlaadronde bij een gelijkblijvende club kan worden overgeslagen. Bewust een los, pure klasse
/// zonder Blazor-afhankelijkheid: testbaar zonder renderer.
/// </summary>
public sealed class ClubWisselTracker
{
    private string? _club;

    /// <summary>Legt vast voor welke club de pagina nu is geladen (aanroepen bij initialisatie).</summary>
    public void Markeer(string? clubCode) => _club = clubCode;

    /// <summary>
    /// <c>true</c> als <paramref name="clubCode"/> afwijkt van de club waarvoor is geladen; de nieuwe
    /// club wordt dan meteen onthouden. <c>false</c> als de club gelijk is: niets herladen.
    /// </summary>
    public bool MoetHerladen(string? clubCode)
    {
        if (string.Equals(_club, clubCode, StringComparison.Ordinal)) return false;
        _club = clubCode;
        return true;
    }
}
