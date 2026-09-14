namespace BlazorAdmin.Services;

/// <summary>
/// Bewaakt of een asynchrone lookup nog bij de actuele selectie hoort (#1136).
///
/// Blazor-componenten heractiveren op elke onvoltooide <c>await</c> in dezelfde synchronisatiecontext
/// (https://learn.microsoft.com/aspnet/core/blazor/components/synchronization-context) — dat
/// voorkomt geen enkele race tussen twee opeenvolgende lookups. Zonder deze guard kan een trage
/// eerdere lookup (A) na een snellere latere lookup (B) alsnog de resultaten van B overschrijven:
/// de gebruiker ziet dan team B geselecteerd, maar de contacten/ontvangers van team A — waardoor
/// een verzending het verkeerde team kan bereiken.
///
/// Elke <see cref="Start"/> verhoogt een monotone teller en levert een token. Een voltooiing telt
/// alleen als "actueel" (<see cref="IsActueel"/>) zolang er sindsdien geen nieuwere <see cref="Start"/>
/// is geweest — dus ook als die nieuwere lookup zelf nog niet is voltooid. Zo overschrijft een
/// verouderde voltooiing nooit de staat.
/// </summary>
public sealed class LookupGeneratieGuard
{
    private int _generatie;

    /// <summary>De selectie waarvoor de meest recente <see cref="Start"/> is aangeroepen.</summary>
    public string? ActueleSelectie { get; private set; }

    /// <summary>
    /// Meldt het begin van een nieuwe lookup voor <paramref name="selectie"/> en levert het token
    /// dat bij voltooiing aan <see cref="IsActueel"/> moet worden voorgelegd.
    /// </summary>
    public int Start(string? selectie)
    {
        ActueleSelectie = selectie;
        return ++_generatie;
    }

    /// <summary>
    /// True als <paramref name="token"/> nog bij de nieuwste <see cref="Start"/>-aanroep hoort —
    /// het resultaat van deze lookup mag dan worden toegepast. False betekent: een nieuwere lookup
    /// is inmiddels gestart, dit resultaat is verouderd en moet worden genegeerd.
    /// </summary>
    public bool IsActueel(int token) => token == _generatie;
}
