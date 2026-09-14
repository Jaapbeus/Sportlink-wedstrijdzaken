using System.Text.Json;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Rauwe vormcontrole van een Sportlink <c>Match</c>-JSON-respons (#998, epic #986) — puur, geen
/// I/O. Bestaat naast <see cref="SportlinkMatch"/> omdat <c>System.Text.Json</c> een ontbrekend of
/// hernoemd veld stilzwijgend op de default laat vallen (bijv. <c>false</c>/<c>null</c>) in plaats
/// van een fout te geven: gewoon deserialiseren en op exceptions wachten zou een contractwijziging
/// van Sportlink dus NIET detecteren. Deze klasse controleert daarom expliciet, op JSON-root-niveau,
/// of elk veld waarop <see cref="SportlinkMatch"/> vertrouwt aanwezig is met het verwachte JSON-type.
/// <para>
/// Daalt bewust NOOIT af in <c>matchOfficials</c> of andere persoonsgebonden structuren — dit is een
/// contractcheck, geen datavalidatie, en de AVG-regel "geen officials-/spelersdata loggen" geldt
/// onverkort. Het resultaat bevat uitsluitend veldNAMEN, nooit waarden.
/// </para>
/// </summary>
public static class SportlinkMatchContract
{
    /// <summary>Verwacht JSON-veldtype per rootveld — meerdere toegestane soorten voor velden waar
    /// Sportlink live wisselvallig gedrag heeft laten zien (zie <c>FlexibleStringJsonConverter</c>/
    /// <c>MatchDateJsonConverter</c> in <see cref="SportlinkMatch"/>).</summary>
    private static readonly Dictionary<string, JsonValueKind[]> VerwachteVelden = new(StringComparer.Ordinal)
    {
        ["publicMatchId"] = new[] { JsonValueKind.String },
        ["externalMatchId"] = new[] { JsonValueKind.String, JsonValueKind.Number },
        ["matchDate"] = new[] { JsonValueKind.Object, JsonValueKind.String },
        ["matchStatus"] = new[] { JsonValueKind.String },
        ["isHomeMatch"] = new[] { JsonValueKind.True, JsonValueKind.False },
        ["isCanceledMatch"] = new[] { JsonValueKind.True, JsonValueKind.False },
        ["isConceptMatch"] = new[] { JsonValueKind.True, JsonValueKind.False },
        ["isEditFieldAllowed"] = new[] { JsonValueKind.True, JsonValueKind.False },
        ["isAssignDressingRoomsAllowed"] = new[] { JsonValueKind.True, JsonValueKind.False },
        ["isAssignOfficialsAllowed"] = new[] { JsonValueKind.True, JsonValueKind.False },
        ["isEditFieldSidePanelAllowed"] = new[] { JsonValueKind.True, JsonValueKind.False },
        ["isAddScoreAllowed"] = new[] { JsonValueKind.True, JsonValueKind.False },
        ["matchField"] = new[] { JsonValueKind.Object, JsonValueKind.Null },
    };

    /// <summary>
    /// Controleert <paramref name="rawJson"/> (de rauwe respons van <c>GET Match</c>) tegen de
    /// verwachte vorm. Retourneert een lege lijst als alles klopt, anders de namen van elk
    /// ontbrekend of onverwacht-getypeerd veld — nooit waarden.
    /// </summary>
    /// <exception cref="JsonException">Als <paramref name="rawJson"/> geen geldige JSON is — de
    /// aanroeper behandelt dit als "vorm afwijkend" (contract gebroken), niet als een aparte tak.</exception>
    public static IReadOnlyList<string> ControleerVorm(string rawJson)
    {
        var afwijkend = new List<string>();
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            afwijkend.Add("(root is geen JSON-object)");
            return afwijkend;
        }

        foreach (var (veldnaam, toegestaneTypes) in VerwachteVelden)
        {
            if (!root.TryGetProperty(veldnaam, out var element))
            {
                afwijkend.Add(veldnaam);
                continue;
            }

            if (!toegestaneTypes.Contains(element.ValueKind))
                afwijkend.Add(veldnaam);
        }

        return afwijkend;
    }
}
