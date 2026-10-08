using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>Kolommen van de Teamaliassen-tabel die de beheerder kan sorteren (#1549).</summary>
public enum TeamAliasSorteerKolom
{
    Geen = 0,
    AantalKeerGebruikt,
    Aangemaakt
}

/// <summary>
/// Sorteerstand en -regel voor de Teamaliassen-tabel (#1549). Losgetrokken uit
/// <c>TeamAliassen.razor.cs</c> zodat de regel unit-testbaar is zonder pagina.
/// <para>
/// Eerste klik op een kolomkop sorteert oplopend, een tweede klik op dezelfde kop aflopend, een
/// derde weer oplopend. Een klik op een andere kolom begint opnieuw oplopend. De sortering is
/// client-side op de reeds geladen lijst: geen nieuwe API-aanroep.
/// </para>
/// <para>
/// De sortering is stabiel: items met een gelijke sleutel houden de volgorde van de API-respons,
/// ook bij aflopend. Een ontbrekende datum (<c>null</c>) komt in beide richtingen achteraan, zodat
/// "geen waarde" nooit onverwacht bovenaan staat.
/// </para>
/// </summary>
public sealed class TeamAliasSortering
{
    public TeamAliasSorteerKolom Kolom { get; private set; } = TeamAliasSorteerKolom.Geen;

    public bool Oplopend { get; private set; } = true;

    /// <summary>Verwerkt een klik op een kolomkop: nieuwe kolom = oplopend, dezelfde kolom = richting omkeren.</summary>
    public void Klik(TeamAliasSorteerKolom kolom)
    {
        if (kolom == TeamAliasSorteerKolom.Geen)
            return;

        if (Kolom == kolom)
        {
            Oplopend = !Oplopend;
        }
        else
        {
            Kolom = kolom;
            Oplopend = true;
        }
    }

    /// <summary>
    /// Waarde voor het <c>aria-sort</c>-attribuut van de kolomkop. <c>null</c> voor een niet-actieve kolom: Blazor laat
    /// het attribuut dan weg, zodat er hoogstens één kop per tabel <c>aria-sort</c> draagt (WAI-ARIA 1.2).
    /// </summary>
    public string? AriaSort(TeamAliasSorteerKolom kolom) =>
        Kolom != kolom ? null : Oplopend ? "ascending" : "descending";

    /// <summary>Zichtbaar teken naast de kolomtitel; leeg zolang de kolom niet actief is.</summary>
    public string Indicator(TeamAliasSorteerKolom kolom) =>
        Kolom != kolom ? "" : Oplopend ? "▲" : "▼";

    /// <summary>Sorteert een kopie van de lijst volgens de huidige stand; zonder actieve kolom blijft de volgorde ongemoeid.</summary>
    public List<TeamAliasDto> Sorteer(IEnumerable<TeamAliasDto> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        switch (Kolom)
        {
            case TeamAliasSorteerKolom.AantalKeerGebruikt:
                return (Oplopend
                        ? items.OrderBy(i => i.AantalKeerGebruikt)
                        : items.OrderByDescending(i => i.AantalKeerGebruikt))
                    .ToList();

            case TeamAliasSorteerKolom.Aangemaakt:
                var lijst = items.ToList();
                var metDatum = lijst.Where(i => i.MtaInserted.HasValue);
                var zonderDatum = lijst.Where(i => !i.MtaInserted.HasValue);
                var gesorteerd = Oplopend
                    ? metDatum.OrderBy(i => i.MtaInserted)
                    : metDatum.OrderByDescending(i => i.MtaInserted);
                return gesorteerd.Concat(zonderDatum).ToList();

            default:
                return items.ToList();
        }
    }
}
