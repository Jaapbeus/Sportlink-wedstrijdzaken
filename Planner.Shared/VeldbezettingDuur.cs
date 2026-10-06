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
/// Dit geldt uitsluitend voor de weergave van wat er al gepland staat. De optimalisatie
/// (<see cref="FieldScheduler"/>, besluit #291) rekent nog met de speeltijdentabel inclusief buffer.
/// </para>
/// </summary>
public static class VeldbezettingDuur
{
    /// <summary>Minuten die de Sportlink-veldplanner bovenop de netto speelduur als bezetting toont.</summary>
    public const int SportlinkBezettingBovenopSpeelduur = 15;

    /// <summary>
    /// Een wedstrijd van deze duur of korter (35+/VR30+: één keer twintig minuten, dag-/avondtoernooien)
    /// is een "korte wedstrijd" met een eigen bezettingsregel, zie <see cref="KorteWedstrijdBezettingBovenopSpeelduur"/>.
    /// </summary>
    public const int KorteWedstrijdMaxSpeelduur = 20;

    /// <summary>
    /// Bezetting bovenop de speelduur van een korte wedstrijd: tien minuten rust tussen twee wedstrijden.
    /// <para>
    /// Eigenaarsbesluit (#1561): een 35+-wedstrijd is één keer twintig minuten met tien minuten rust,
    /// en er start elk halfuur een wedstrijd — een blok van dertig minuten. Met de vijftien minuten van
    /// de lange wedstrijden werd het 35 en overlapten opeenvolgende blokken vijf minuten. <b>Niet
    /// nagemeten op de Sportlink-veldplanner</b> (de vijftien is gemeten op wedstrijden van 50 tot 90
    /// minuten); controleer dit op een toernooi-/35+-speeldag, zie #1560.
    /// </para>
    /// </summary>
    public const int KorteWedstrijdBezettingBovenopSpeelduur = 10;

    /// <param name="sportlinkSpeelduur">Netto speelduur volgens Sportlink, of <c>null</c>/0 als onbekend.</param>
    /// <param name="speeltijdenTotaal">Terugval uit de speeltijdentabel, of <c>null</c> als de categorie ontbreekt.</param>
    /// <returns>Bezettingsduur in minuten; 0 als geen van beide bronnen iets weet.</returns>
    public static int Bepaal(int? sportlinkSpeelduur, int? speeltijdenTotaal)
        => sportlinkSpeelduur is > 0
            ? sportlinkSpeelduur.Value + (sportlinkSpeelduur.Value <= KorteWedstrijdMaxSpeelduur
                ? KorteWedstrijdBezettingBovenopSpeelduur
                : SportlinkBezettingBovenopSpeelduur)
            : Math.Max(speeltijdenTotaal ?? 0, 0);
}
