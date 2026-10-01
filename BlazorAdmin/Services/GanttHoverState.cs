namespace BlazorAdmin.Services;

/// <summary>
/// Hover-correlatie tussen een Gantt-tijdlijnblok en de bijbehorende tabelrij, gesleuteld op
/// WedstrijdCode (oorspronkelijk #1315 op Planning, generiek gemaakt bij #1398 zodat Planning,
/// Veld optimalisatie en toekomstige pagina's met dezelfde Gantt+tabel-combinatie één
/// implementatie delen in plaats van elk hun eigen hoverveld). Eén instantie per pagina — geen
/// DI-service, gewoon een <c>new()</c>-veld in de code-behind, zelfde opzet als
/// <see cref="SportlinkActieKolomState"/>.
///
/// De bijbehorende CSS (<c>.gantt-blok-hover</c>, <c>.gantt-rij-hover</c>) staat gedeeld in
/// <c>wwwroot/css/app.css</c> — Blazor's CSS-isolatie scopet een <c>.razor.css</c> per component,
/// dus een klasse die meerdere pagina's moeten delen hoort in het globale stylesheet, niet
/// gedupliceerd in elk gescopeerd bestand.
/// </summary>
public sealed class GanttHoverState
{
    public long? WedstrijdCode { get; private set; }

    public void Zet(long? wedstrijdCode) => WedstrijdCode = wedstrijdCode;

    public void Wis() => WedstrijdCode = null;

    private bool IsActief(long? wedstrijdCode) => wedstrijdCode.HasValue && wedstrijdCode == WedstrijdCode;

    /// <summary>CSS-klasse voor een Gantt-blok — leeg als dit niet de gehoverde wedstrijd is.</summary>
    public string BlokKlasse(long? wedstrijdCode) => IsActief(wedstrijdCode) ? "gantt-blok-hover" : "";

    /// <summary>CSS-klasse voor een tabelrij — leeg als dit niet de gehoverde wedstrijd is.</summary>
    public string RijKlasse(long? wedstrijdCode) => IsActief(wedstrijdCode) ? "gantt-rij-hover" : "";
}
