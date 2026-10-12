using System.Net.Http;
using System.Text;

namespace Planner.Shared.Infrastructure;

// Het begrensd lezen van een antwoordbody, als apart deel van SsrfProtection (#1675): de
// redirect- en connectielogica en het lezen van de body zijn twee losse verantwoordelijkheden.

/// <summary>Gedeeld bytebudget voor alle gelezen antwoorden van één samenhangende ophaalactie.</summary>
public sealed class ResponseBudget
{
    public ResponseBudget(int bytes) => Remaining = bytes;

    /// <summary>Aantal bytes dat nog gelezen mag worden.</summary>
    public int Remaining { get; private set; }

    internal void Consume(int bytes) => Remaining -= bytes;
}

/// <summary>
/// Gooit <see cref="SsrfProtection.ReadBoundedStringAsync"/> als een antwoordbody groter is dan de
/// toegestane omvang. Bewust geen <see cref="SsrfBlockedException"/>: de bestemming is toegestaan,
/// alleen het antwoord is te groot om te verwerken.
/// </summary>
public sealed class ResponseTooLargeException : Exception
{
    public ResponseTooLargeException() : base("Antwoord is te groot.") { }
}

public static partial class SsrfProtection
{
    /// <summary>
    /// Leest de body als tekst en stopt bij <paramref name="maxBytes"/>: een groter antwoord geeft
    /// een <see cref="ResponseTooLargeException"/> in plaats van volledig in het geheugen te komen.
    /// De bytes worden gedecodeerd met de charset uit de <c>Content-Type</c>-header, met UTF-8 als
    /// terugval (zie <see cref="BepaalEncoding"/>).
    /// </summary>
    public static async Task<string> ReadBoundedStringAsync(
        HttpContent content, int maxBytes = MaxResponseBytes, CancellationToken ct = default,
        ResponseBudget? budget = null)
    {
        if (budget != null) maxBytes = Math.Min(maxBytes, budget.Remaining);
        if (content.Headers.ContentLength is { } lengte && lengte > maxBytes)
            throw new ResponseTooLargeException();

        using var bron = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var blok = new byte[8192];
        int gelezen;
        while ((gelezen = await bron.ReadAsync(blok, ct)) > 0)
        {
            if (buffer.Length + gelezen > maxBytes)
                throw new ResponseTooLargeException();
            buffer.Write(blok, 0, gelezen);
        }
        budget?.Consume((int)buffer.Length);
        var encoding = BepaalEncoding(content.Headers.ContentType?.CharSet);
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// De encoding bij een charset-naam uit de <c>Content-Type</c>-header. Leeg of onbekend geeft
    /// UTF-8. Naast de ingebouwde encodings (UTF-8, UTF-16, ISO-8859-1, ...) worden ook de
    /// Windows-codepagina's herkend, zonder de provider globaal te registreren.
    /// </summary>
    internal static Encoding BepaalEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
            return Encoding.UTF8;

        var naam = charset.Trim().Trim('"', '\'');
        try
        {
            return Encoding.GetEncoding(naam);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(naam) ?? Encoding.UTF8;
        }
    }
}
