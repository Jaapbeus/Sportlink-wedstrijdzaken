using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Sorteervolgorde voor de wedstrijdenlijst onder de Dagplanning-Gantt (#1331). Losgetrokken uit
/// <c>Dagplanning.razor.cs</c> zodat de sorteerregel — net als <see cref="GanttLayout"/> voor de
/// Gantt-balken — unit-testbaar is zonder de code-behind/pagina op te tuigen.
/// <para>
/// <b>Vóór deze fix</b> had <c>GefilterdeLijst()</c> geen eigen sortering: de zichtbare volgorde
/// hing af van de volgorde in de AutoPlan-API-respons. Primaire sleutel is de optimale
/// aanvangstijd, oplopend; secundaire sleutel is <see cref="AutoPlanWedstrijdItemDto.OptimaalVeldNummer"/>
/// — de interne, clubgebonden veldvolgorde uit <c>public.velden.veldnummer</c>
/// (<c>PlannerSettingsRepository.GetVeldenAsync</c>, per club gesorteerd op dat veldnummer).
/// </para>
/// <para>
/// <b>Nooit <see cref="AutoPlanWedstrijdItemDto.OptimaalVeldNaam"/> als sorteersleutel.</b> Dat is
/// de vrije, door de club geconfigureerde displaynaam (A, B, C net zo geldig als "Veld 1", "Veld 2")
/// en heeft geen alfabetische of numerieke ordeningsbetekenis — sorteren op die naam zou een club
/// met letternamen een willekeurige (lexicografische) volgorde geven in plaats van de geconfigureerde
/// veldvolgorde.
/// </para>
/// </summary>
public static class DagplanningSortering
{
    /// <summary>
    /// Sorteert op geparseerde <see cref="TimeOnly"/> (vroeg naar laat), en bij gelijke tijd op
    /// <see cref="AutoPlanWedstrijdItemDto.OptimaalVeldNummer"/> oplopend. Een ontbrekende of niet
    /// te parsen tijd, en een ontbrekend veldnummer, krijgen elk een vaste plek ná elke geldige
    /// waarde — deterministisch, en nooit onverwacht bovenaan (acceptatiecriterium #1331).
    /// </summary>
    public static IOrderedEnumerable<AutoPlanWedstrijdItemDto> Sorteer(
        IEnumerable<AutoPlanWedstrijdItemDto> items) =>
        items.OrderBy(TijdSleutel).ThenBy(VeldSleutel);

    private static TimeOnly TijdSleutel(AutoPlanWedstrijdItemDto item) =>
        TimeOnly.TryParse(item.OptimaalTijd, out var tijd) ? tijd : TimeOnly.MaxValue;

    private static int VeldSleutel(AutoPlanWedstrijdItemDto item) =>
        item.OptimaalVeldNummer ?? int.MaxValue;
}
