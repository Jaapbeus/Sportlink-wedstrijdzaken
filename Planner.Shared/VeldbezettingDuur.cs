namespace Planner.Shared;

/// <summary>
/// Hoe lang een al geplande wedstrijd het veld bezet op de veldbezettingsweergave (#1547) —
/// tier-agnostisch, zodat beide tiers en de deel-export (HTML/PDF) exact dezelfde regel volgen.
/// <para>
/// <b>Sportlink is leidend.</b> Sportlink levert per wedstrijd de netto speelduur
/// (<c>/wedstrijd-informatie</c> → <c>duration</c>, bijvoorbeeld 60 voor 2×30). De
/// Sportlink-veldplanner tekent elk blok als die speelduur plus vijftien minuten — nagemeten op
/// acht wedstrijden van één speeldag, van 50 (→ 65) tot 90 (→ 105) minuten speelduur, ook voor
/// categorieën waarvoor de eigen speeltijdentabel een rust van tien minuten kent. Precies dat getal
/// toont de app dus ook.
/// </para>
/// <para>
/// Pas als Sportlink voor een wedstrijd géén speelduur kent (geen wedstrijd-informatie, of 0),
/// valt de duur terug op <c>WedstrijdTotaal</c> uit de speeltijdentabel van de club. Dat was tot
/// #1547 de enige bron, en leverde voor een team met een afwijkende speelduur (bijvoorbeeld een
/// veteranenteam, dat als senioren 115 minuten kreeg in plaats van Sportlinks 75) een blok op dat
/// niets met de werkelijke bezetting te maken had.
/// </para>
/// <para>
/// Eén regel voor elke wedstrijd, ook voor 35+ en VR30+ (één keer twintig minuten): de rust tussen twee
/// wedstrijden is geen speeltijd en telt dus niet apart mee (eigenaarsbesluit #1561). Een uitzondering per
/// duur is bewust niet gebouwd. Sinds #1563 neemt de Planning de exacte blokduur rechtstreeks uit de Sportlink-veldplanner over (<c>VeldplannerOverlayCore</c>); deze regel is daarmee alleen nog de terugval als Sportlink niet bereikbaar is of de wedstrijd niet kent.
/// </para>
/// <para>
/// Dit geldt uitsluitend voor de weergave van wat er al gepland staat. De optimalisatie
/// (<see cref="FieldScheduler"/>, besluit #291) rekent nog met de speeltijdentabel inclusief buffer.
/// </para>
/// </summary>
public static class VeldbezettingDuur
{
    /// <summary>Minuten die de Sportlink-veldplanner bovenop de netto speelduur als bezetting toont.</summary>
    public const int SportlinkBezettingBovenopSpeelduur = 15;

    /// <param name="sportlinkSpeelduur">Netto speelduur volgens Sportlink, of <c>null</c>/0 als onbekend.</param>
    /// <param name="speeltijdenTotaal">Terugval uit de speeltijdentabel, of <c>null</c> als de categorie ontbreekt.</param>
    /// <returns>Bezettingsduur in minuten; 0 als geen van beide bronnen iets weet.</returns>
    public static int Bepaal(int? sportlinkSpeelduur, int? speeltijdenTotaal)
        => sportlinkSpeelduur is > 0
            ? sportlinkSpeelduur.Value + SportlinkBezettingBovenopSpeelduur
            : Math.Max(speeltijdenTotaal ?? 0, 0);
}
