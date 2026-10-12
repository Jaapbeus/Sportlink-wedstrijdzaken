using Microsoft.AspNetCore.Mvc;

namespace Planner.Endpoints.Admin;

/// <summary>
/// Gedeelde 503-respons voor endpoints die een externe dienst aanroepen terwijl de EgressGuard
/// (#857) uitgaand verkeer niet toestaat. De tiers leveren hun eigen <c>EgressGuard</c> aan.
/// </summary>
public static class UitgaandVerkeerCore
{
    /// <summary>Geeft <c>null</c> als uitgaand verkeer is toegestaan, anders het 503-antwoord.</summary>
    public static IActionResult? Blokkeer(Func<bool> uitgaandVerkeerToegestaan) =>
        uitgaandVerkeerToegestaan()
            ? null
            : new ObjectResult(new { error = "Uitgaande integraties zijn buiten productie uitgeschakeld" }) { StatusCode = 503 };
}
