using Cronos;
using Microsoft.AspNetCore.Mvc;

namespace Planner.Endpoints.Admin;

/// <summary>
/// De inhoudelijke validatie van <c>PUT /api/beheer/settings</c> (#1459), gedeeld door beide
/// <c>AdminSettingsFunction</c>-bestanden. Tot #1459 stond deze controle woordelijk in beide tiers
/// (codekwaliteitsregel 1); het toevoegen van de PDF-export-schakelaar maakte die kopie zichtbaar in
/// de tier-duplicatiemeting. Per tier blijft alleen de witte lijst van velden (de kolommen verschillen
/// per tier) en het wegschrijven.
/// </summary>
public static class AppSettingsValidatieCore
{
    /// <summary>Geldige waarden voor <c>KnvbStandaardRegio</c> — de PK-waarden van de KNVB-kalendertabel.</summary>
    public static readonly IReadOnlyList<string> GeldigeKnvbRegios =
        ["West", "Noord", "Oost", "Zuid", "Landelijk", "LandelijkJeugd"];

    /// <summary>Breedte van de kolom <c>SportlinkSpelactiviteit</c> (#1437).</summary>
    public const int MaxSpelactiviteitLengte = 100;

    /// <summary>
    /// Geeft de 400-respons voor de eerste ongeldige wijziging, of <c>null</c> als alles klopt.
    /// <paramref name="changes"/> bevat alleen velden die de tier al door zijn witte lijst heeft gelaten.
    /// </summary>
    public static IActionResult? Valideer(IReadOnlyDictionary<string, string?> changes)
    {
        if (changes.TryGetValue("FetchSchedule", out var nieuweSchedule) && nieuweSchedule != null
            && !CronExpression.TryParse(nieuweSchedule, CronFormat.IncludeSeconds, out _))
            return Fout($"Ongeldige CRON-expressie: '{nieuweSchedule}'. Verwacht 6 velden (seconden minuten uren dag maand weekdag).");

        if (changes.TryGetValue("KnvbStandaardRegio", out var nieuweRegio)
            && !string.IsNullOrWhiteSpace(nieuweRegio)
            && !GeldigeKnvbRegios.Contains(nieuweRegio, StringComparer.Ordinal))
            return Fout($"Ongeldige KnvbStandaardRegio: '{nieuweRegio}'. Toegestaan: {string.Join(", ", GeldigeKnvbRegios)}.");

        // #1437: de kolom is 100 tekens breed; een te lange waarde geeft anders een databasefout (500).
        if (changes.TryGetValue("SportlinkSpelactiviteit", out var nieuweActiviteit) && nieuweActiviteit is { Length: > MaxSpelactiviteitLengte })
            return Fout($"Spelactiviteit mag maximaal {MaxSpelactiviteitLengte} tekens bevatten.");

        // #1459: de kolom is NOT NULL; alleen een expliciete aan/uit-waarde is geldig.
        if (changes.TryGetValue("PdfExportIngeschakeld", out var nieuwePdf) && nieuwePdf is not ("0" or "1" or "true" or "false"))
            return Fout("PdfExportIngeschakeld moet 0/1 (aan/uit) zijn.");

        return null;
    }

    private static BadRequestObjectResult Fout(string melding) => new(new { error = melding });
}
