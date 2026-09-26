using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// FeatureKey-constanten voor de per-club, per-rol instelbare zichtbaarheid van Sportlink-acties
/// (#1341, epic #1338) — het ENE vertaalpunt tussen <see cref="SportlinkMutationSoort"/> en de
/// FeatureKey-waarde in <c>public.rolfeatureinstellingen</c>/<c>dbo.RolFeatureInstellingen</c>,
/// zodat geen tier of endpoint zijn eigen stringliteral verzint.
/// </summary>
public static class SportlinkRolFeature
{
    public const string Kleedkamers = "sportlink.kleedkamers";
    public const string Scheidsrechter = "sportlink.scheidsrechter";
    public const string Veld = "sportlink.veld";

    /// <summary>Alle instelbare FeatureKeys, in de volgorde waarin de instellingenpagina ze toont.</summary>
    public static readonly IReadOnlyList<string> Alle = new[] { Kleedkamers, Scheidsrechter, Veld };

    /// <summary>
    /// Vertaalt een <see cref="SportlinkMutationSoort"/> naar de bijbehorende FeatureKey, of
    /// <c>null</c> als die mutatiesoort geen instelbare toggle heeft (bijv.
    /// <see cref="SportlinkMutationSoort.DatumTijdAccommodatie"/> — daar bestaat bewust geen
    /// per-rol-toggle voor, zie issue #1341).
    /// </summary>
    public static string? VoorMutatieSoort(SportlinkMutationSoort soort) => soort switch
    {
        SportlinkMutationSoort.Kleedkamers => Kleedkamers,
        SportlinkMutationSoort.Officials => Scheidsrechter,
        SportlinkMutationSoort.Veld => Veld,
        _ => null
    };

    /// <summary>
    /// Voegt aan de rauwe <see cref="SportlinkMatch"/>-respons de opgeloste
    /// <see cref="SportlinkRolFeatureToestemmingen"/> toe (#1341) — puur UX, de server-side
    /// mutatie-endpoints blijven de echte controle uitvoeren. <c>System.Text.Json.Nodes</c>, niet
    /// Newtonsofts <c>JObject</c>: <see cref="SportlinkMatch"/> is geannoteerd met
    /// <c>System.Text.Json</c>'s <c>[JsonPropertyName]</c>, en Azure Functions' eigen
    /// <c>OkObjectResult</c>-serialisatie gebruikt <c>System.Text.Json</c> — Newtonsofts
    /// <c>JObject.FromObject</c> zou de PascalCase C#-eigenschapsnamen teruggeven i.p.v. de
    /// bestaande camelCase wire-vorm.
    /// </summary>
    public static JsonObject VoegToestemmingenToe(SportlinkMatch match, SportlinkRolFeatureToestemmingen toestemmingen)
        => VoegToestemmingenToe(JsonSerializer.SerializeToNode(match)!.AsObject(), toestemmingen);

    /// <summary>
    /// Overload voor een al bestaande <see cref="JsonObject"/> (#1341, na samenvoeging met #1339) —
    /// zodat de toestemmingen-vlaggen kunnen worden toegevoegd aan de respons die
    /// <c>SportlinkFieldIdBuilder.BouwPaneelResponse</c> al opbouwde (veldOpties/subpositieOpties),
    /// zonder <see cref="SportlinkMatch"/> een tweede keer te serialiseren.
    /// </summary>
    public static JsonObject VoegToestemmingenToe(JsonObject payload, SportlinkRolFeatureToestemmingen toestemmingen)
    {
        payload["kleedkamersFeatureToegestaan"] = toestemmingen.Kleedkamers;
        payload["scheidsrechterFeatureToegestaan"] = toestemmingen.Scheidsrechter;
        payload["veldFeatureToegestaan"] = toestemmingen.Veld;
        return payload;
    }
}

/// <summary>
/// Wat de HUIDIGE aanroeper mag voor deze club (#1341) — al opgelost (admin-bypass + DB-lookup
/// verwerkt), puur nog om door te geven aan de UI zodat een knop verborgen/disabled kan worden
/// vóórdat de gebruiker 'm probeert. De server (<c>SportlinkMatchFunction.ExecuteMutationAsync</c>)
/// blijft de echte, leidende controle — dit is uitsluitend UX.
/// </summary>
public sealed record SportlinkRolFeatureToestemmingen(bool Kleedkamers, bool Scheidsrechter, bool Veld);
