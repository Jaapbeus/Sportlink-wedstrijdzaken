using Planner.Shared.Email.Trace;

namespace Planner.Shared.Email;

/// <summary>Gevalideerde, gesaneerde invoer van een admin-leermoment (#1568 deel C).</summary>
public sealed record LeermomentGeldig(string OrigineelType, string JuistType, string Samenvatting);

/// <summary>
/// De ene plek die bepaalt wat een admin als leermoment mag aanleveren: een bekend verzoektype en een
/// korte samenvatting die door <see cref="TraceBuilder.Saneer"/> is gehaald — dezelfde PII-arme
/// functie als de trace, geen tweede sanitizer. De samenvatting belandt in de few-shot-prompt van de
/// classificatie en wordt voor admin-leermomenten permanent bewaard.
/// </summary>
public static class LeermomentInvoer
{
    public const int MaxSamenvattingLengte = 500;
    public const string OnbekendType = "Onbekend";

    /// <summary>
    /// Namen van <c>VerzoekType</c>. De enum staat per tier; een test per tier bewaakt dat deze lijst
    /// gelijk blijft aan <c>Enum.GetNames&lt;VerzoekType&gt;()</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> GeldigeVerzoekTypes =
        ["BeschikbaarheidCheck", "HerplanVerzoek", "Bevestiging", "TeamContactOpvragen", "BuitenScope"];

    public static (LeermomentGeldig? Waarde, string? Fout) Valideer(string? origineel, string? juist, string? samenvatting)
    {
        var juistType = (juist ?? "").Trim();
        if (!GeldigeVerzoekTypes.Contains(juistType, StringComparer.Ordinal))
            return (null, $"Ongeldig juist verzoektype. Gebruik een van: {string.Join(", ", GeldigeVerzoekTypes)}.");

        var origineelType = (origineel ?? "").Trim();
        if (origineelType.Length == 0) origineelType = OnbekendType;
        else if (!GeldigeVerzoekTypes.Contains(origineelType, StringComparer.Ordinal))
            return (null, $"Ongeldig origineel verzoektype. Laat leeg of gebruik een van: {string.Join(", ", GeldigeVerzoekTypes)}.");

        // Aanhalingstekens vallen weg: de samenvatting staat in de prompt tussen dubbele aanhalingstekens.
        var schoon = TraceBuilder.Saneer(samenvatting, MaxSamenvattingLengte).Replace('"', '\'');
        if (schoon.Length == 0)
            return (null, "Samenvatting is verplicht (korte omschrijving van het bericht, zonder namen of adressen).");

        return (new LeermomentGeldig(origineelType, juistType, schoon), null);
    }

    /// <summary>
    /// Eén regel van de few-shot-sectie (#1568 deel C). Een reply-correctie heeft een samenvatting van het
    /// origineel én van de correctie; een admin-leermoment alleen één door de beheerder geredigeerde
    /// samenvatting (geen correctietekst) en soms geen bekend origineel type. Gedeeld door beide tiers, zodat de
    /// prompt op beide gelijk is.
    /// </summary>
    public static string FewShotRegel(string origineelType, string juistType, string? origineleSamenvatting, string? correctieSamenvatting)
    {
        var o = origineleSamenvatting ?? "";
        if (!string.IsNullOrWhiteSpace(o) && string.IsNullOrWhiteSpace(correctieSamenvatting))
            return origineelType == OnbekendType || origineelType == juistType
                ? $"- Samenvatting: \"{o}\" → is een {juistType}."
                : $"- Samenvatting: \"{o}\" → was geclassificeerd als {origineelType}, maar was eigenlijk {juistType}.";

        return $"- Samenvatting: \"{o}\" → was geclassificeerd als {origineelType}, maar was eigenlijk {juistType}. Correctie: \"{correctieSamenvatting}\"";
    }
}
