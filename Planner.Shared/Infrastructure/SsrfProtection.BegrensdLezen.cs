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
    /// Een byte order mark bepaalt de encoding en wordt niet in de tekst opgenomen; zonder BOM
    /// geldt de charset uit de <c>Content-Type</c>-header, met UTF-8 als terugval (zie
    /// <see cref="HerkenBom"/> en <see cref="BepaalEncoding"/>).
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
        var bytes = new ReadOnlySpan<byte>(buffer.GetBuffer(), 0, (int)buffer.Length);
        var (bomEncoding, bomLengte) = HerkenBom(bytes);
        var encoding = bomEncoding ?? BepaalEncoding(content.Headers.ContentType?.CharSet);
        return encoding.GetString(bytes[bomLengte..]);
    }

    /// <summary>
    /// Herkent een byte order mark voor UTF-8, UTF-16 (LE/BE) en UTF-32 (LE/BE) en geeft de
    /// bijbehorende encoding en de lengte van de BOM; zonder BOM <c>(null, 0)</c>. UTF-32LE wordt
    /// vóór UTF-16LE getoetst, omdat die BOM met dezelfde twee bytes begint.
    /// </summary>
    internal static (Encoding? Encoding, int Lengte) HerkenBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4);
        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return (Encoding.UTF8, 3);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
            return (Encoding.Unicode, 2);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
            return (Encoding.BigEndianUnicode, 2);
        return (null, 0);
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
