using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email;

/// <summary>Begrenst de lengte van een berichttekst vóór die naar de AI-provider gaat.</summary>
public static class BerichtBodyBegrenzing
{
    /// <summary>Maximaal aantal tekens van een body dat in een AI-aanroep terechtkomt.</summary>
    public const int MaxBodyTekens = 8000;

    public const string AfkapMarkering = "\n[... bericht ingekort ...]";

    public static string Kap(string? body, int maxTekens = MaxBodyTekens)
    {
        if (string.IsNullOrEmpty(body)) return "";
        if (body.Length <= maxTekens) return body;

        // Geen halve surrogaatparen achterlaten.
        var lengte = char.IsHighSurrogate(body[maxTekens - 1]) ? maxTekens - 1 : maxTekens;
        return body[..lengte] + AfkapMarkering;
    }

    /// <summary>
    /// <see cref="Kap(string?, int)"/>, met een logregel als de tekst wordt ingekort, zodat zichtbaar is dat
    /// de AI niet het hele bericht zag. De log noemt alleen lengtes, nooit inhoud.
    /// </summary>
    public static string Kap(string? body, ILogger? log)
    {
        if (body is not null && body.Length > MaxBodyTekens)
            log?.LogInformation("Berichttekst van {Lengte} tekens ingekort tot {Max} tekens vóór de AI-aanroep", body.Length, MaxBodyTekens);
        return Kap(body);
    }
}
