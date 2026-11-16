using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Email;

namespace Planner.Endpoints.Admin;

/// <summary>Een e-mailwijziging vereist het volledige schema; overige instellingen slaan de probe over.</summary>
public static class EmailInstellingenSchemaControle
{
    public static async Task<IActionResult?> ControleerAsync(
        IDictionary<string, string?> changes,
        Func<Task<bool>> emailKolommenBeschikbaar,
        string veiligeSchemaMelding)
    {
        if (EmailVerwerkingsInstellingenWijziging.BevatEmailVeld(changes)
            && !await emailKolommenBeschikbaar())
            return new ConflictObjectResult(new { error = veiligeSchemaMelding });
        return null;
    }
}
