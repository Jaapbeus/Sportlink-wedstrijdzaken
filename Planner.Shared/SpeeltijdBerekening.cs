namespace Planner.Shared;

/// <summary>
/// De duur van een wedstrijd volgens de speeltijden van de club (#1962, WZ-ADR-014): aantal helften ×
/// helftduur + rust. Er zit geen buffer in; de buffer staat op de optimalisatiepagina. Eén regel voor beide
/// databasetiers en de Admin GUI, zodat het opgeslagen totaal altijd uit de drie invoervelden volgt.
/// </summary>
public static class SpeeltijdBerekening
{
    /// <summary>Standaard: twee helften. Toernooivormen zoals 35+ en VR30+ spelen er één.</summary>
    public const int StandaardHelften = 2;

    /// <summary>
    /// Bovengrens voor helft, rust en totaal: één dag. Begrenst ook de rekenkunde, zodat extreme invoer nooit
    /// overloopt naar een totaal van 0 of negatief (review #1966 R1-F1).
    /// </summary>
    public const int MaxMinuten = 24 * 60;

    /// <summary>
    /// Invoer van de speeltijden-API (#1962): ontbrekende helften worden <see cref="StandaardHelften"/>; geeft het
    /// aantal helften, het berekende totaal en een foutmelding (of <c>null</c>) terug.
    /// </summary>
    public static (int Helften, int Totaal, string? Fout) Bereken(int? helften, int helftMinuten, int rustMinuten)
    {
        var h = helften ?? StandaardHelften;
        var fout = Valideer(h, helftMinuten, rustMinuten);
        return (h, fout == null ? Totaal(h, helftMinuten, rustMinuten) : 0, fout);
    }

    /// <summary>Het totaal; roep eerst <see cref="Valideer"/> aan. Loopt nooit stil over (<c>checked</c>).</summary>
    public static int Totaal(int helften, int helftMinuten, int rustMinuten) =>
        checked(helften * helftMinuten + rustMinuten);

    /// <summary>
    /// Controleert de invoer. Geeft een Nederlandse foutmelding terug, of <c>null</c> als de invoer klopt.
    /// Eén helft heeft geen rust: een rust groter dan 0 bij één helft is een invoerfout.
    /// </summary>
    public static string? Valideer(int helften, int helftMinuten, int rustMinuten)
    {
        if (helften is not (1 or 2)) return "Aantal helften moet 1 of 2 zijn";
        if (helftMinuten <= 0) return "Helft (min) moet groter zijn dan 0";
        if (rustMinuten < 0) return "Rust (min) mag niet negatief zijn";
        if (helften == 1 && rustMinuten > 0) return "Bij één helft is er geen rust";
        if (helftMinuten > MaxMinuten || rustMinuten > MaxMinuten)
            return $"Helft en rust mogen elk hoogstens {MaxMinuten} minuten zijn";
        if ((long)helften * helftMinuten + rustMinuten > MaxMinuten)
            return $"Totaal mag hoogstens {MaxMinuten} minuten zijn";
        return null;
    }
}
