namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// De vier velddelen van het formulier "Wedstrijd aanmaken" (#1437) en hun <c>FieldSize</c>-waarde
/// in de <c>ClubMatch</c>-body.
/// <para>
/// <b>AANNAME — nog niet live bevestigd.</b> Alleen <c>"1.0"</c> (heel veld) is live vastgesteld
/// (#1427). De notatie voor een half, kwart en achtste veld (<c>"0.5"</c>, <c>"0.25"</c>,
/// <c>"0.125"</c>) is geëxtrapoleerd uit die ene waarde en moet nog met een echte wedstrijd worden
/// gecontroleerd. <c>FieldOffset "0"</c> betekent: het eerste deel van het veld. Zie
/// docs/SPORTLINK-WEB-EXTENSION.md.
/// </para>
/// </summary>
public static class ClubMatchVelddeel
{
    public const string Heel = "1.0";
    public const string Half = "0.5";
    public const string Kwart = "0.25";
    public const string Achtste = "0.125";

    /// <summary>De toegestane waarden, in volgorde van groot naar klein.</summary>
    public static readonly IReadOnlyList<string> Waarden = new[] { Heel, Half, Kwart, Achtste };

    public static bool IsGeldig(string? waarde) => waarde != null && Waarden.Contains(waarde);

    /// <summary>
    /// Vertaalt de veldafmeting uit de speeltijden-tabel (1.00 / 0.50 / 0.25 / 0.125) naar een
    /// velddeel; onbekend of afwijkend → <c>null</c> (de aanroeper valt terug op <see cref="Heel"/>).
    /// </summary>
    public static string? VanAfmeting(decimal? afmeting) => afmeting switch
    {
        1.0m => Heel,
        0.5m => Half,
        0.25m => Kwart,
        0.125m => Achtste,
        _ => null,
    };
}
