namespace Planner.Shared.Tests.Avg;

/// <summary>
/// Eén gedeelde tabel met invoer en verwachting voor de datumselectie van de his-retentie (#1698,
/// Codex-review ronde 2): welke <c>wedstrijddatum</c>-waarden gelden als datum en worden bij grens
/// <see cref="Grens"/> leeggemaakt. Dezelfde tabel draait in de unit-test van
/// <c>HisPersoonsgegevensRetentie</c> én in beide database-integratiesuites (gelinkt bronbestand in
/// <c>FunctionApp.Tests</c> en <c>Database.Postgres.Tests</c>), zodat de tiers niet uiteen kunnen
/// lopen. Uitsluitend synthetische waarden, geen persoonsgegevens.
/// <para>
/// Contract: gelijke selectie en niet raden. Alleen strikt <c>yyyy-MM-dd</c> in de eerste tien tekens,
/// met ASCII-cijfers en een bestaande kalenderdatum, telt; al het andere blijft staan.
/// </para>
/// </summary>
internal static class HisWedstrijddatumGevallen
{
    /// <summary>De seizoensgrens waartegen elke verwachting is opgesteld.</summary>
    public static readonly DateOnly Grens = new(2026, 6, 30);

    /// <summary>(waarde, wordt gewist, reden). De reden staat in de foutmelding van de test.</summary>
    public static readonly IReadOnlyList<(string? Waarde, bool Wissen, string Reden)> Alle =
    [
        ("2026-06-30T14:30:00+02:00", true, "laatste dag van het afgeronde seizoen"),
        ("2026-06-30", true, "precies tien tekens"),
        ("2026-06-30 14:30", true, "spatie als scheiding na de datum"),
        ("2026-06-30   ", true, "spaties achter de datum"),
        ("2024-02-29T10:00:00+01:00", true, "geldige schrikkeldag"),
        ("1999-12-31T10:00:00+01:00", true, "oud seizoen"),
        ("0001-01-01", true, "laagste datum van beide datumtypes"),
        ("2026-07-01T14:30:00+02:00", false, "na de grens"),
        ("9999-12-31", false, "hoogste datum, na de grens"),
        ("2025-02-29T10:00:00+01:00", false, "29 februari buiten een schrikkeljaar"),
        ("2026-02-30T10:00:00+01:00", false, "onmogelijke datum"),
        ("2026-04-31", false, "april heeft 30 dagen"),
        ("2026-13-01", false, "maand 13"),
        ("2026-00-10", false, "maand 0"),
        ("2026-01-00", false, "dag 0"),
        ("0000-01-01", false, "jaar 0"),
        ("onbekend", false, "geen datum"),
        ("", false, "lege tekst"),
        (null, false, "NULL"),
        ("2026-06-3", false, "korter dan tien tekens"),
        ("2026-6-30T10:00", false, "maand met één cijfer"),
        ("2026-06-5 10:00", false, "dag met één cijfer, tijdsdeel erachter"),
        (" 2026-06-30", false, "spatie vóór de datum"),
        ("\t2026-06-30", false, "tab vóór de datum"),
        ("2026/06/30", false, "schuine strepen"),
        ("2026.06.30", false, "punten"),
        ("2026 06 30", false, "spaties als scheiding"),
        ("2026-06-30".Replace('-', '\u2010'), false, "Unicode-koppelteken"),
        ("20260630", false, "zonder scheidingstekens"),
        ("20260630T1430", false, "ISO-basisvorm met tijd"),
        ("30-06-2026", false, "dag-maand-jaar"),
        ("06/30/2026", false, "maand/dag/jaar"),
        ("+2026-06-30", false, "plusteken vóór het jaar"),
        ("2026-06-30".Replace('2', '\uFF12'), false, "fullwidth cijfers (gelijk aan ASCII onder een breedte-ongevoelige collation)"),
        ("\u0662\u0660\u0662\u0666-06-30", false, "Arabisch-Indische cijfers"),
        ("\U0001D7D0026-06-30", false, "wiskundig vet cijfer buiten het BMP"),
        ("2026-06-30".Replace('2', '\u00B2'), false, "superscript twee (valt onder sommige collations binnen LIKE [0-9])"),
    ];
}
