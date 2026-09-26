using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Vertaalt onze eigen veldconfiguratie (<c>public.velden</c>/<c>dbo.Velden</c>, VeldNummer +
/// subpositie) naar een VOORSTEL voor Sportlink's write-side <c>FieldId</c>/<c>FieldSize</c>
/// (#1339, epic #986) — tegenovergestelde richting van <see cref="VeldResolver"/> (die vertaalt
/// Sportlink-tekst NAAR onze VeldNummer).
/// <para>
/// <b>Dit is een voorstel, geen bevestigde resolutie.</b> Het patroon
/// <c>"{FacilityId}-OUTDOOR_FIELD-{VeldNummer}"</c> is bevestigd voor precies één combinatie
/// (de vaste testwedstrijd, wedstrijdnummer 69, veld 6 → <c>"…-OUTDOOR_FIELD-6"</c>, zie
/// <c>Planner.Shared.Tests</c>/<c>docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md</c>). Of
/// Sportlinks eigen veldnummering voor élke club exact gelijk loopt aan onze VeldNummer-kolom is
/// NIET bevestigd — een coding agent mag dit nooit zelf verifiëren (zie
/// docs/SPORTLINK-WEB-EXTENSION.md §4.4). De aanroeper toont dit voorstel daarom altijd naast de
/// bewerkbare FieldId/FieldSize-velden, nooit als enige, onbewerkbare waarde.
/// </para>
/// </summary>
public static class SportlinkFieldIdBuilder
{
    /// <summary>Bekende subpositie-labels, inclusief <c>null</c> voor "heel veld" — zelfde
    /// vocabulaire als <see cref="VeldResolver.SubpositieFractie"/>, hier als vaste, opsombare
    /// lijst voor een keuzelijst in de UI.</summary>
    public static readonly IReadOnlyList<string?> BekendeSubposities =
        new string?[] { null, "A", "B", "A1", "A2", "B1", "B2" };

    /// <summary>Bouwt het voorgestelde <c>FieldId</c>, of <c>null</c> als <paramref name="facilityId"/>
    /// ontbreekt (bijv. wedstrijd nog niet aan een accommodatie gekoppeld).</summary>
    public static string? BouwVoorstelFieldId(string? facilityId, int veldNummer) =>
        string.IsNullOrWhiteSpace(facilityId) ? null : $"{facilityId}-OUTDOOR_FIELD-{veldNummer}";

    /// <summary>Bouwt de voorgestelde <c>FieldSize</c>-string (bijv. <c>"1.0"</c>, <c>"0.5"</c>,
    /// <c>"0.25"</c>) voor een subpositie — <c>null</c>/onbekende subpositie geeft <c>"1.0"</c>
    /// (heel veld), consistent met <see cref="VeldResolver.SubpositieFractie"/>'s
    /// <c>null</c>-betekenis "geen subpositie".</summary>
    public static string BouwVoorstelFieldSize(string? subpositie) =>
        // Geen expliciete "0.0"-opmaak: decimal-literalen (1.0m/0.5m/0.25m) behouden hun eigen
        // schaal, dus ToString(CultureInfo.InvariantCulture) geeft al exact "1.0"/"0.5"/"0.25" —
        // een vast aantal decimalen zou 0.25 juist afronden naar "0.3".
        (VeldResolver.SubpositieFractie(subpositie) ?? 1.0m).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Bouwt de volledige <c>GET .../sportlink/match/{wedstrijdcode}</c>-respons (#1339) —
    /// tier-onafhankelijk, want dit is pure data-transformatie, geen databasetoegang en geen
    /// HTTP-vertaling (docs/ARCHITECTUUR-CODEKWALITEIT.md regel 1). Was aanvankelijk in beide
    /// <c>SportlinkMatchFunction.cs</c>-bestanden gekopieerd (`check-tier-duplicatie.sh` sloeg
    /// meteen aan); nu één implementatie die beide tiers aanroepen.
    /// <para>
    /// <b>Bewust <c>System.Text.Json.Nodes</c>, niet Newtonsofts <c>JObject</c>:</b>
    /// <see cref="SportlinkMatch"/> is geannoteerd met <c>System.Text.Json</c>'s
    /// <c>[JsonPropertyName]</c> (<c>"publicMatchId"</c>, <c>"matchField"</c>, ...), en Azure
    /// Functions' eigen <c>OkObjectResult</c>-serialisatie gebruikt <c>System.Text.Json</c>.
    /// Newtonsofts <c>JObject.FromObject</c> kent die attributen niet en zou de PascalCase
    /// C#-eigenschapsnamen teruggeven (<c>"PublicMatchId"</c>) — een stille contractbreuk t.o.v.
    /// de bestaande respons.
    /// </para>
    /// <para>
    /// De node-merge (i.p.v. elk <see cref="SportlinkMatch"/>-veld hier met de hand overtypen)
    /// voorkomt dat een toekomstig nieuw veld op <see cref="SportlinkMatch"/> stilzwijgend uit
    /// deze respons wegvalt. <c>fieldId</c>/<c>fieldSize</c> komen bewust ook plat op het
    /// rootniveau (naast het geneste <c>field</c>-object) — <c>SportlinkMatchInfoDto</c>
    /// (BlazorAdmin) bindt op die platte vorm.
    /// </para>
    /// </summary>
    public static JsonObject BouwPaneelResponse(SportlinkMatch match, IEnumerable<(int VeldNummer, string VeldNaam)> velden)
    {
        var facilityId = match.MatchField?.FacilityId;
        var payload = JsonSerializer.SerializeToNode(match)!.AsObject();
        payload["fieldId"] = match.Field?.FieldId;
        payload["fieldSize"] = match.Field?.FieldSize;
        payload["veldOpties"] = JsonSerializer.SerializeToNode(
            velden.Select(v => new SportlinkVeldOptie(
                v.VeldNummer, v.VeldNaam, BouwVoorstelFieldId(facilityId, v.VeldNummer))).ToList());
        payload["subpositieOpties"] = JsonSerializer.SerializeToNode(
            BekendeSubposities.Select(s => new SportlinkSubpositieOptie(s, BouwVoorstelFieldSize(s))).ToList());
        return payload;
    }
}

/// <summary>Eén club-veld met het voorgestelde <c>FieldId</c> voor de accommodatie van de huidige
/// wedstrijd (#1339) — gebruikt om de veld-dropdown in <c>SportlinkMatchPanel</c> te vullen.</summary>
public sealed record SportlinkVeldOptie(int VeldNummer, string VeldNaam, string? VoorstelFieldId);

/// <summary>Eén subpositie-keuze met de bijbehorende voorgestelde <c>FieldSize</c> (#1339).
/// <c>Subpositie == null</c> betekent "heel veld".</summary>
public sealed record SportlinkSubpositieOptie(string? Subpositie, string VoorstelFieldSize);
