namespace BlazorAdmin.Services;

/// <summary>
/// Laadt voor het hoofdmenu de toegestane sleutels en de extensievlag uit <c>GET api/planner/menu-toegang</c>
/// (via <see cref="ClubInstellingenCache"/>, #1599/#1647). Een antwoord wordt alleen toegepast als er sindsdien
/// geen nieuwere lading is gestart (generatie): bij een snelle clubwissel mag het late antwoord van de vorige
/// club de stand van de nieuwe club nooit overschrijven.
/// </summary>
public sealed class MenuStandLader
{
    private readonly ClubInstellingenCache _cache;
    private readonly ClubSelectorService _club;
    private int _generatie;
    private string? _geladenVoorClub;
    private bool _heeftGeladen;

    public MenuStandLader(ClubInstellingenCache cache, ClubSelectorService club)
    {
        _cache = cache;
        _club = club;
    }

    /// <summary>Toegestane menu-sleutels; <c>null</c> zolang ze ontbreken of de aanroep mislukte (fail-closed).</summary>
    public IReadOnlyList<string>? Sleutels { get; private set; }

    /// <summary>Laadt voor de gekozen club, tenzij dat al gebeurd is. Retourneert of dit antwoord is toegepast.</summary>
    public async Task<bool> LaadAsync()
    {
        var clubCode = _club.SelectedClubCode;
        // Nog geen club gekozen (de layout is nog bezig): wachten op de clubwissel i.p.v. een overbodige aanroep
        // zonder club te doen; ClubSelectorService.OnChange start de lading zodra de club bekend is.
        if (clubCode is null) return false;
        if (_heeftGeladen && _geladenVoorClub == clubCode) return false;
        _heeftGeladen = true;
        _geladenVoorClub = clubCode;
        var generatie = ++_generatie;
        Sleutels = null;

        var toegang = await _cache.GetMenuToegangAsync();
        if (generatie != _generatie) return false;

        Sleutels = toegang.Success ? toegang.Data?.FeatureKeys : null;
        _club.ZetSportlinkExtensionEnabled(toegang.Success && toegang.Data?.SportlinkExtensieActief == true);
        return true;
    }
}
