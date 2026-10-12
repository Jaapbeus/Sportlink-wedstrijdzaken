using System;
using System.Globalization;

namespace Planner.Shared.Scheduling;

// #1623: de ene bron van waarheid voor "cron-expressie naar Nederlandse tekst".
//
// Gecompileerd in Planner.Shared én als gelinkt bronbestand in BlazorAdmin (zie BlazorAdmin.csproj),
// daarom UITSLUITEND afhankelijk van de BCL (check-gelinkte-bronbestanden.sh).

/// <summary>
/// Vertaalt 6-velds cron-expressies (Azure-formaat: sec min uur dag maand weekdag) naar leesbare
/// Nederlandse tekst. Wat niet herkend wordt komt ongewijzigd terug; lege invoer geeft "".
/// </summary>
public static class CronLeesbaar
{
    private static readonly string[] DagNamen =
        { "zondag", "maandag", "dinsdag", "woensdag", "donderdag", "vrijdag", "zaterdag", "zondag" };

    private readonly record struct Velden(string Sec, string Min, string Uur, string Dag, string Maand, string Week);

    public static string Vertaal(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return "";
        var delen = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (delen.Length != 6) return cron;
        var v = new Velden(delen[0], delen[1], delen[2], delen[3], delen[4], delen[5]);
        if (v.Sec != "0") return cron;

        return Dagelijks(v) ?? ElkUur(v) ?? Interval(v) ?? Maandelijks(v) ?? Weekdag(v) ?? cron;
    }

    private static string? Dagelijks(Velden v)
        => v.Dag == "*" && v.Maand == "*" && v.Week == "*" && Getal(v.Uur, out var uur) && Getal(v.Min, out var min)
            ? $"Elke dag om {uur:D2}:{min:D2}" : null;

    private static string? ElkUur(Velden v)
    {
        if (v.Uur != "*" || v.Dag != "*" || v.Maand != "*" || v.Week != "*" || !Getal(v.Min, out var min)) return null;
        return min == 0 ? "Elk uur" : $"Elk uur op minuut :{min:D2}";
    }

    private static string? Interval(Velden v)
        => v.Uur == "*" && v.Dag == "*" && v.Maand == "*" && v.Week == "*"
           && v.Min.StartsWith("*/", StringComparison.Ordinal) && Getal(v.Min[2..], out var n) && n > 0
            ? $"Elke {n} minuten" : null;

    private static string? Maandelijks(Velden v)
        => v.Maand == "*" && v.Week == "*"
           && Getal(v.Min, out var min) && Getal(v.Uur, out var uur) && Getal(v.Dag, out var dag)
            ? $"Maandelijks op dag {dag} om {uur:D2}:{min:D2}" : null;

    private static string? Weekdag(Velden v)
        => v.Dag == "*" && v.Maand == "*"
           && Getal(v.Min, out var min) && Getal(v.Uur, out var uur) && Getal(v.Week, out var wd) && wd <= 6
            ? $"Elke {DagNamen[wd]} om {uur:D2}:{min:D2}" : null;

    private static bool Getal(string tekst, out int waarde)
        => int.TryParse(tekst, NumberStyles.None, CultureInfo.InvariantCulture, out waarde);
}
