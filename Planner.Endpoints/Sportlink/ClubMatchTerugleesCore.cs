using System.Text.Json;
using Microsoft.Extensions.Logging;
using Planner.Shared;
using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Endpoints.Sportlink;

/// <summary>
/// #1892: na een echte clubwedstrijd-aanmaak de wedstrijd bij Sportlink teruglezen en waarschuwen als
/// Sportlink een ander uitteam toont dan de beheerder opgaf (WZ-ADR-013: Sportlink is bron van waarheid,
/// afwijkingen zichtbaar). Sinds #1871 kon het eigen team ongemerkt als uitteam wegkomen; dit vangt zo'n
/// afwijking direct op in het resultaatpaneel.
/// <para>
/// Tier-onafhankelijk: beide databasetiers roepen <see cref="VoegTerugleesWaarschuwingToeAsync"/> aan.
/// Kost één extra read-only <c>Match</c>-GET per echte aanmaak (geen dry-run). De teruglezing is
/// best-effort: een fout, een onbekende vorm of een ontbrekend veld geeft nooit een waarschuwing en breekt
/// de respons nooit af — de wedstrijd is dan al aangemaakt.
/// </para>
/// </summary>
public static class ClubMatchTerugleesCore
{
    /// <summary>
    /// Geeft <paramref name="waarschuwingen"/> terug, aangevuld met één waarschuwing als Sportlink na een
    /// geslaagde, echte aanmaak een ander uitteam toont. Doet niets (en leest niets terug) bij een mislukking,
    /// dry-run, gesimuleerde aanroep of zonder bruikbaar <c>PublicMatchId</c>.
    /// </summary>
    public static async Task<IReadOnlyList<string>> VoegTerugleesWaarschuwingToeAsync(
        ISportlinkClubClient client, string rolNaam, SportlinkMutationResult? resultaat,
        SportlinkClubMatchAanvraag aanvraag, IReadOnlyList<string> waarschuwingen, ILogger log)
    {
        if (resultaat is not { IsSuccess: true, IsDryRun: false, IsForcedDryRun: false }
            || string.IsNullOrWhiteSpace(resultaat.PublicMatchId))
            return waarschuwingen;

        try
        {
            var antwoord = await client.GetMatchRawJsonAsync(rolNaam, resultaat.PublicMatchId);
            if (antwoord.Status != SportlinkClubCallStatus.Ok || string.IsNullOrWhiteSpace(antwoord.Data))
            {
                // Alleen de status loggen, nooit de body of een URL.
                log.LogInformation("Teruglezen van de aangemaakte clubwedstrijd overgeslagen (status {Status}).", antwoord.Status);
                return waarschuwingen;
            }

            var melding = BeoordeelUitteam(antwoord.Data, aanvraag);
            return melding == null ? waarschuwingen : waarschuwingen.Append(melding).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Geen uitzonderingsobject loggen: dat kan de aanvraag of Sportlink-details bevatten.
            log.LogWarning("Teruglezen van de aangemaakte clubwedstrijd mislukt ({Type}).", ex.GetType().Name);
            return waarschuwingen;
        }
    }

    /// <summary>
    /// Vergelijkt het uitteam in Sportlinks <c>Match</c>-respons (<c>Teams.Away</c>) met de aanvraag.
    /// <c>null</c> = geen afwijking of niet te beoordelen. Een gekoppeld uitteam (eigen <c>T…</c>-ID) wordt op
    /// ID vergeleken; een externe tegenstander (sentinel) op genormaliseerde naam, tenzij Sportlink hem juist
    /// aan het eigen thuisteam koppelt.
    /// </summary>
    public static string? BeoordeelUitteam(string matchJson, SportlinkClubMatchAanvraag aanvraag)
    {
        string? uitNaam, uitId;
        try
        {
            using var doc = JsonDocument.Parse(matchJson);
            if (!doc.RootElement.TryGetProperty("Teams", out var teams)
                || teams.ValueKind != JsonValueKind.Object
                || !teams.TryGetProperty("Away", out var away)
                || away.ValueKind != JsonValueKind.Object)
                return null;
            uitNaam = Tekst(away, "TeamName");
            uitId = Tekst(away, "PublicTeamId");
        }
        catch (JsonException)
        {
            return null;
        }

        var gevraagdGekoppeld = !string.Equals(aanvraag.PublicAwayTeamId, SportlinkClubMatchAanvraag.GeenTeamId, StringComparison.Ordinal);
        if (gevraagdGekoppeld)
        {
            // Zonder ID uit Sportlink valt niets te beoordelen: geen valse waarschuwing.
            if (string.IsNullOrWhiteSpace(uitId)
                || string.Equals(uitId, aanvraag.PublicAwayTeamId, StringComparison.Ordinal))
                return null;
        }
        else
        {
            // De geen-team-sentinel is bij een vrij thuisteam óók het thuis-ID: dat is geen eigen team.
            var eigenAlsUit = !string.IsNullOrWhiteSpace(uitId)
                && !string.Equals(uitId, SportlinkClubMatchAanvraag.GeenTeamId, StringComparison.Ordinal)
                && string.Equals(uitId, aanvraag.PublicTeamId, StringComparison.Ordinal);
            if (!eigenAlsUit && (string.IsNullOrWhiteSpace(uitNaam) || Zelfde(uitNaam, aanvraag.AwayTeam)))
                return null;
        }

        return $"Sportlink toont als uitteam '{uitNaam ?? "onbekend"}', maar er is '{aanvraag.AwayTeam}' opgegeven. "
             + "Controleer de wedstrijd in Sportlink Club (Sportlink is leidend).";
    }

    private static bool Zelfde(string a, string b)
        => string.Equals(
            TeamNaamNormalisatie.NormaliseerVoorVergelijking(a),
            TeamNaamNormalisatie.NormaliseerVoorVergelijking(b),
            StringComparison.OrdinalIgnoreCase);

    private static string? Tekst(JsonElement o, string naam)
        => o.TryGetProperty(naam, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
