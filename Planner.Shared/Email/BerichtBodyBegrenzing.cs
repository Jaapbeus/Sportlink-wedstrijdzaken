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
}
