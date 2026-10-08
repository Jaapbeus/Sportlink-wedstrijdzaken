using System.Text.Json;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Eén wedstrijdblok zoals de Sportlink-veldplanner het tekent (#1563): veld, starttijd en de volledige
/// bezetting — uit Sportlink overgenomen, niet berekend.
/// </summary>
/// <param name="Label">"Thuis - Uit" met de teamnamen zoals Sportlink ze toont; sleutel voor de koppeling.</param>
/// <param name="Veld">Veldnaam in onze notatie: "veld 2", of "veld 2 B" bij een deel van het veld.</param>
/// <param name="StartTijd">"HH:mm", inclusief het eventuele inloopdeel (<c>StartUpInterval</c>).</param>
/// <param name="Veldafmeting">1, 0,5 of 0,25.</param>
/// <param name="DuurMinuten">Inloop + speelduur + pauze + uitloop — precies de breedte van het blok.</param>
public sealed record SportlinkVeldplannerBlok(
    string Label, string Veld, string StartTijd, decimal Veldafmeting, int DuurMinuten, string? PublicMatchId);

/// <summary>Vertaalt de respons van <c>competition/facilityoccupation/FacilityOccupation</c> naar blokken.</summary>
public static class SportlinkVeldplannerParser
{
    /// <summary>Sportlinks eigen standaardduur als <c>Duration</c> ontbreekt (zie de frontend-bundle).</summary>
    public const int StandaardDuur = 90;

    /// <summary>Alleen <c>ScheduledMatches</c>; een item zonder veld of starttijd wordt overgeslagen.
    /// Gooit <see cref="JsonException"/> bij ongeldige JSON — de aanroeper vertaalt dat naar een fout.</summary>
    public static IReadOnlyList<SportlinkVeldplannerBlok> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var resultaat = new List<SportlinkVeldplannerBlok>();
        if (!doc.RootElement.TryGetProperty("ScheduledMatches", out var lijst) || lijst.ValueKind != JsonValueKind.Array)
            return resultaat;

        foreach (var m in lijst.EnumerateArray())
        {
            var start = Tekst(m, "StartTimeAsString");
            if (start == null || !TimeOnly.TryParse(start, out var starttijd)) continue;
            if (!m.TryGetProperty("Field", out var veld) || veld.ValueKind != JsonValueKind.Object) continue;
            var veldNaam = Tekst(veld, "FieldName");
            if (veldNaam == null) continue;

            var afmeting = veld.TryGetProperty("FieldSize", out var fs) && fs.ValueKind == JsonValueKind.Number
                ? fs.GetDecimal() : 1m;
            var deel = Tekst(veld, "FieldOffsetDescription");
            var veldTekst = afmeting < 1m && deel != null ? $"{veldNaam} {deel}" : veldNaam;

            var duur = Getal(m, "Duration") ?? StandaardDuur;
            var inloop = Getal(m, "StartUpInterval") ?? 0;
            var totaal = inloop + duur + (Getal(m, "Interval") ?? 0) + (Getal(m, "FollowUpInterval") ?? 0);
            var begin = starttijd.AddMinutes(-inloop);

            resultaat.Add(new SportlinkVeldplannerBlok(
                $"{Team(m, "Home")} - {Team(m, "Away")}", veldTekst, begin.ToString("HH:mm"), afmeting, totaal,
                Tekst(m, "PublicMatchId")));
        }
        return resultaat;
    }

    private static string Team(JsonElement m, string kant)
        => m.TryGetProperty("Teams", out var t) && t.TryGetProperty(kant, out var k) ? Tekst(k, "TeamName") ?? "" : "";

    private static string? Tekst(JsonElement e, string naam)
        => e.TryGetProperty(naam, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Getal(JsonElement e, string naam)
        => e.TryGetProperty(naam, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
