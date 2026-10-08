namespace BlazorAdmin.Services;

/// <summary>
/// Bepaalt wanneer de layout de pagina (<c>Body</c>) mag renderen (#1578), zodat een pagina nooit
/// clubgebonden gegevens laadt vóórdat de effectieve clubkeuze vaststaat. Anders laadt elke pagina
/// eerst zonder club en nogmaals zodra de club bekend wordt. Pure klasse zonder Blazor-afhankelijkheid.
/// </summary>
/// <remarks>
/// De poort is vrij als de opslag is uitgelezen (<see cref="MarkeerOpslagGelezen"/>, ook na een
/// time-out of fout) én er óf een club bekend is, óf het ophalen van de clublijst is afgerond
/// (<see cref="MarkeerClublijstAfgerond"/>, ook als dat mislukt of leeg is), zodat de pagina's hun
/// eigen foutafhandeling kunnen tonen en het scherm nooit eeuwig leeg blijft.
/// </remarks>
public sealed class PaginaStartPoort
{
    private bool _opslagGelezen;
    private bool _clublijstAfgerond;
    private string? _clubCode;

    public bool IsVrij => _opslagGelezen && (!string.IsNullOrWhiteSpace(_clubCode) || _clublijstAfgerond);

    public void MarkeerOpslagGelezen(string? clubCode)
    {
        _opslagGelezen = true;
        _clubCode = clubCode;
    }

    public void MarkeerClublijstAfgerond(string? clubCode)
    {
        _clublijstAfgerond = true;
        _clubCode = clubCode;
    }
}
