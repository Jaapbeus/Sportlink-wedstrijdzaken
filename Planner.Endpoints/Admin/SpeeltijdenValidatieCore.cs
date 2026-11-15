using Microsoft.AspNetCore.Mvc;
using Planner.Shared;

namespace Planner.Endpoints.Admin;

/// <summary>
/// Invoercontrole van <c>POST/PUT /api/beheer/speeltijden</c> (#1962), gedeeld door beide
/// <c>AdminSpeeltijdenFunction</c>-bestanden (zelfde opzet als <see cref="AppSettingsValidatieCore"/>). Controleert
/// helften, helft, rust en het berekende totaal (hoogstens één dag, geen overflow; review R1-F1). Bij een fout geeft de
/// function 400 en keert terug vóór de repository-aanroep, dus er wordt niets opgeslagen.
/// </summary>
public static class SpeeltijdenValidatieCore
{
    public static BadRequestObjectResult? Valideer(int? helften, int helft, int rust, out int aantal, out int totaal)
    {
        string? fout;
        (aantal, totaal, fout) = SpeeltijdBerekening.Bereken(helften, helft, rust);
        return fout == null ? null : new BadRequestObjectResult(new { error = fout });
    }
}
