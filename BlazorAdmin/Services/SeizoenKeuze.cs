namespace BlazorAdmin.Services;

/// <summary>Seizoenskeuze voor "Volledig seizoen opnieuw opbouwen" (#1461).</summary>
public static class SeizoenKeuze
{
    /// <summary>Het KNVB-seizoen start in de zomer: vóór 1 juli hoort het startjaar bij het vorige kalenderjaar.</summary>
    public static int HuidigStartjaar(DateTime vandaag) => vandaag.Month >= 7 ? vandaag.Year : vandaag.Year - 1;

    /// <summary>Vijf seizoenen, nieuwste eerst, eindigend bij het huidige seizoen: een seizoen in de
    /// toekomst heeft niets om opnieuw op te bouwen.</summary>
    public static IReadOnlyList<int> ResetOpties(DateTime vandaag)
    {
        var huidig = HuidigStartjaar(vandaag);
        return [.. Enumerable.Range(huidig - 4, 5).Reverse()];
    }
}
