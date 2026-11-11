using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email;

/// <summary>Begrenst de lengte van een berichttekst vóór die naar de AI-provider gaat.</summary>
public static class BerichtBodyBegrenzing
{
    /// <summary>Maximaal aantal tekens van een body dat in een AI-aanroep terechtkomt.</summary>
    public const int MaxBodyTekens = 8000;

    public const string AfkapMarkering = "\n[... bericht ingekort ...]";

    /// <summary>
    /// Maskeert uitsluitend een begrensd bodyvenster. Bij afkappen vervalt een mogelijk onafgemaakt
    /// contactfragment, ook als dit gewone tekst blijkt; de rest van een lange body wordt niet gescand.
    /// </summary>
    public static string VoorProvider(string? body, ILogger? log = null)
    {
        if (string.IsNullOrEmpty(body)) return "";

        var lengte = VeiligePrefixLengte(body);
        var gemaskeerd = AiVeldSanering.MaskeerContactgegevens(body[..lengte]);
        // Maskeringen kunnen korte patronen verlengen. Behoud ook dan de kap en hele tokens.
        var uitvoerLengte = VeiligePrefixLengte(gemaskeerd);
        var ingekort = lengte < body.Length || uitvoerLengte < gemaskeerd.Length;
        if (ingekort)
            log?.LogInformation("Berichttekst van {Lengte} tekens ingekort tot maximaal {Max} tekens vóór de AI-aanroep", body.Length, MaxBodyTekens);
        return gemaskeerd[..uitvoerLengte] + (ingekort ? AfkapMarkering : "");
    }

    private static int VeiligePrefixLengte(string tekst)
    {
        if (tekst.Length <= MaxBodyTekens) return tekst.Length;

        var lengte = MaxBodyTekens;
        // Het e-mailpatroon kan elk niet-witruimtetoken omvatten. Lees nooit voorbij de grens
        // om het af te maken: laat het overlappende token weg, inclusief een eventuele maskering.
        if (!char.IsWhiteSpace(tekst[MaxBodyTekens]))
            while (lengte > 0 && !char.IsWhiteSpace(tekst[lengte - 1])) lengte--;

        // Het telefoonpatroon mag spaties bevatten; alleen teruggaan naar de vorige spatie
        // zou eerdere cijfergroepen laten staan. Onderzoek uitsluitend het bestaande venster.
        if (IsNummerVervolg(tekst[MaxBodyTekens]))
        {
            var nummerStart = MaxBodyTekens;
            while (nummerStart > 0 && (IsNummerVervolg(tekst[nummerStart - 1]) || tekst[nummerStart - 1] == '+'))
                nummerStart--;
            lengte = Math.Min(lengte, nummerStart);
        }

        if (lengte > 0 && char.IsHighSurrogate(tekst[lengte - 1])) lengte--;
        return lengte;
    }

    private static bool IsNummerVervolg(char teken)
        => char.IsDigit(teken) || teken is '(' or ')' or '.' or '-'
            || char.GetUnicodeCategory(teken) == UnicodeCategory.SpaceSeparator;

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
