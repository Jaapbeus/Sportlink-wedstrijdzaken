using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Planner.Shared.Infrastructure;

// Provider-onafhankelijke SSRF-bescherming, gedeeld tussen beide database-tiers (#1130). Was tot
// deze verhuizing een byte-voor-byte identieke kopie in FunctionApp/Infrastructure/SsrfProtection.cs
// en FunctionApp.Postgres/Infrastructure/SsrfProtection.cs (alleen de namespace verschilde) — precies
// het patroon dat docs/ARCHITECTUUR-DATABASE-TIERS.md §2 als gedeelde, pure logica toestaat. Zelfde
// precedent als TeamNaamNormalisatie/VeldResolver/LeeftijdNormalisatie: geen DB, geen tier-specifieke
// afhankelijkheid, dus één implementatie in plaats van twee.

/// <summary>
/// Gooit deze uitzondering als een uitgaande HTTP-bestemming door <see cref="SsrfProtection"/>
/// geweigerd wordt (privé/loopback/link-local IP, ongewenste poort of te veel redirects).
/// </summary>
public sealed class SsrfBlockedException : Exception
{
    public SsrfBlockedException(string message) : base(message) { }
}

/// <summary>Gedeeld bytebudget voor alle gelezen antwoorden van één samenhangende ophaalactie.</summary>
public sealed class ResponseBudget
{
    public ResponseBudget(int bytes) => Remaining = bytes;

    /// <summary>Aantal bytes dat nog gelezen mag worden.</summary>
    public int Remaining { get; private set; }

    internal void Consume(int bytes) => Remaining -= bytes;
}

/// <summary>
/// Centrale SSRF-bescherming voor uitgaande HTTP-aanroepen naar door de admin opgegeven URL's
/// (#1007 — thema-extractor). Los van <see cref="EgressGuard"/>: die schakelt uitgaande
/// integraties in/uit per omgeving (lokaal/CI vs. productie); dit hier bepaalt of een specifieke
/// bestemming binnen een toegestane integratie wél veilig is om te benaderen.
/// <para>
/// Twee aanvalspaden worden gedicht:
/// </para>
/// <list type="number">
/// <item>De standaard <c>HttpClientHandler</c>/<c>SocketsHttpHandler</c> volgt redirects
/// automatisch, zonder de nieuwe bestemming opnieuw te controleren. Hier: <c>AllowAutoRedirect =
/// false</c> plus een begrensde, handmatige redirect-lus (<see cref="GetWithBoundedRedirectsAsync"/>)
/// die op elke hop opnieuw valideert.</item>
/// <item>DNS-rebinding: een losse DNS-check gevolgd door een tweede, aparte resolutie bij de
/// werkelijke connectie laat een aanvaller de eerste (goedgekeurde) resolutie vervangen door een
/// tweede die naar een intern adres wijst. Hier: <see cref="ConnectCoreAsync"/> resolvet zelf, één
/// keer, en verbindt direct met exact dat gevalideerde IP-adres — geen tweede resolutiestap
/// tussen validatie en connectie. Dit is de <c>ConnectCallback</c> van <c>SocketsHttpHandler</c>,
/// dus elke aanroep (initieel én elke redirect-hop) loopt hierdoor.</item>
/// </list>
/// </summary>
public static class SsrfProtection
{
    /// <summary>Maximum aantal redirect-hops dat handmatig gevolgd wordt.</summary>
    public const int MaxRedirects = 5;

    /// <summary>Maximale omvang van een antwoordbody die gelezen wordt (2 MB).</summary>
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    private static readonly HashSet<int> AllowedPorts = new() { 80, 443 };

    public static bool IsAllowedPort(int port) => AllowedPorts.Contains(port);

    /// <summary>
    /// Controleert schema (alleen http/https) en poort (standaard alleen 80/443) van een URI.
    /// Bevat géén IP-validatie — die gebeurt uitsluitend op het moment van verbinden, zie
    /// <see cref="ConnectCoreAsync"/>. <paramref name="isAllowedPortOverride"/> is uitsluitend
    /// voor tests, om een lokale loopback-listener op een niet-standaardpoort te kunnen gebruiken;
    /// productiecode laat dit weg en gebruikt dan altijd <see cref="IsAllowedPort"/>.
    /// </summary>
    public static bool TryValidateUriShape(Uri uri, out string? error, Func<int, bool>? isAllowedPortOverride = null)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Alleen http/https toegestaan.";
            return false;
        }
        var isAllowedPort = isAllowedPortOverride ?? IsAllowedPort;
        if (!isAllowedPort(uri.Port))
        {
            error = $"Poort {uri.Port} is niet toegestaan.";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// True als <paramref name="address"/> een publiek, routeerbaar adres is — false voor
    /// loopback, RFC1918-privéreeksen, link-local (169.254.x.x/fe80::/10), CGNAT (100.64.0.0/10),
    /// unique-local IPv6 (fc00::/7), multicast/gereserveerd, en de IPv4-mapped-IPv6-equivalenten
    /// daarvan (::ffff:10.0.0.1 wordt eerst naar IPv4 uitgepakt).
    /// <para>
    /// IPv6-beleid (#1622): een adres is alleen publiek binnen 2000::/3 (globaal unicast, RFC 4291).
    /// De daaropvolgende prefixcontroles (fc00::/7, link-local, NAT64, IPv4-compatible, enz.) liggen
    /// grotendeels buiten 2000::/3 en zijn daardoor redundant; ze blijven bewust staan als
    /// defense in depth, zodat een latere versoepeling van de poort niet stilzwijgend een bereik opent.
    /// Binnen 2000::/3 blokkeren ze nog 2001:db8::/32, Teredo, 2001:1::/32 en 6to4 met een
    /// niet-publiek ingebed IPv4-adres.
    /// </para>
    /// </summary>
    public static bool IsPublicIpAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsPublicIpv4(address.GetAddressBytes());

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return IsPublicIpv6(address);

        return false;
    }

    /// <summary>IPv4-regels van <see cref="IsPublicIpAddress"/> (bytes van een gewoon IPv4-adres).</summary>
    private static bool IsPublicIpv4(byte[] b)
    {
        if (b[0] == 0) return false;                                   // 0.0.0.0/8
        if (b[0] == 10) return false;                                  // 10.0.0.0/8
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;     // 100.64.0.0/10 CGNAT
        if (b[0] == 127) return false;                                 // 127.0.0.0/8 loopback
        if (b[0] == 169 && b[1] == 254) return false;                  // 169.254.0.0/16 link-local
        if (b[0] == 168 && b[1] == 63 && b[2] == 129 && b[3] == 16) return false; // Azure-platformadres (wireserver)
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;     // 172.16.0.0/12
        if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;       // 192.0.0.0/24 IETF
        if (b[0] == 192 && b[1] == 168) return false;                  // 192.168.0.0/16
        if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;   // 198.18.0.0/15 benchmark
        if (b[0] >= 224) return false;                                 // multicast/gereserveerd/broadcast
        return true;
    }

    /// <summary>IPv6-regels van <see cref="IsPublicIpAddress"/> (adres is geen IPv4-mapped adres meer).</summary>
    private static bool IsPublicIpv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;
        var b = address.GetAddressBytes();
        // Beleidspoort (#1622): alleen globaal unicast 2000::/3 kan publiek zijn (RFC 4291).
        // Alles daarbuiten (::, ::/96-compatible, ::ffff:0:0/96-translated, 64:ff9b::/96 NAT64,
        // 100::/64, fc00::/7, fe80::/10, ff00::/8, ...) is fail-closed geblokkeerd.
        if ((b[0] & 0xE0) != 0x20) return false;
        if ((b[0] & 0xFE) == 0xFC) return false; // fc00::/7 unique local
        if (IsGeblokkeerdIpv6Prefix(b)) return false;

        // Overgangsmechanismen die een IPv4-adres inbedden: beoordeel dat IPv4-adres met
        // dezelfde regels, zodat een privé/gereserveerd IPv4-adres niet via IPv6 binnenkomt.
        if (TryIngebeddeIpv4(b, out var ingebed))
            return IsPublicIpAddress(ingebed);

        return true;
    }

    // Prefix (eerste bytes) + prefixlengte in bits.
    // APPLICATIEBELEID (#1622, eigenaarsbesluit 2026-10-11): conservatieve blokkades binnen 2000::/3.
    // Dit is géén afspiegeling van de IANA-kolom "Globally Reachable": per het IANA IPv6 Special-Purpose
    // Address Registry staan Benchmarking en Documentation op False, ORCHIDv2 en Drone Remote ID op True,
    // en is het veld bij de vervallen ORCHID-toewijzing leeg. Deze app hoeft geen van deze bereiken ooit te
    // bereiken (het zijn protocol-/testtoewijzingen, geen webservers), dus blokkeren is fail-closed en
    // kost niets. Versoepelen alleen met een nieuw eigenaarsbesluit.
    private static readonly (byte[] Prefix, int Bits)[] BeleidsmatigGeblokkeerdBinnenGlobaalUnicast =
    {
        (new byte[] { 0x20, 0x01, 0x00, 0x02, 0x00, 0x00 }, 48),  // 2001:2::/48 Benchmarking (RFC 5180)
        (new byte[] { 0x20, 0x01, 0x00, 0x10 }, 28),  // 2001:10::/28 ORCHID (gedeprecieerd)
        (new byte[] { 0x20, 0x01, 0x00, 0x20 }, 28),  // 2001:20::/28 ORCHIDv2 (RFC 7343)
        (new byte[] { 0x20, 0x01, 0x00, 0x30 }, 28),  // 2001:30::/28 Drone Remote ID (RFC 9374)
        (new byte[] { 0x3F, 0xFF, 0x00 }, 20),        // 3fff::/20 Documentation (RFC 9637)
    };

    private static bool HeeftPrefix(byte[] b, byte[] prefix, int bits)
    {
        for (var i = 0; i < bits / 8; i++)
            if (b[i] != prefix[i]) return false;
        var rest = bits % 8;
        if (rest == 0) return true;
        var masker = (byte)(0xFF << (8 - rest));
        return (b[bits / 8] & masker) == (prefix[bits / 8] & masker);
    }

    /// <summary>
    /// IPv6-prefixen die nooit een publieke bestemming zijn: unspecified (::), discard-only
    /// (100::/64), documentatie (2001:db8::/32), Teredo (2001:0::/32), speciale anycast-toewijzingen (2001:1::/32) en NAT64 voor lokaal
    /// gebruik (64:ff9b:1::/48). Binnen 2000::/3 bovendien, als applicatiebeleid en niet als IANA-classificatie,
    /// de protocol-/testtoewijzingen benchmarking, ORCHID/ORCHIDv2, Drone Remote ID en documentatie 3fff::/20
    /// (zie de toelichting bij de tabel; SRv6 5f00::/16 valt al buiten 2000::/3).
    /// Teredo en NAT64-lokaal worden bewust volledig geblokkeerd in
    /// plaats van de ingebedde IPv4-adressen te ontleden: simpeler en fail-closed.
    /// </summary>
    private static bool IsGeblokkeerdIpv6Prefix(byte[] b)
    {
        if (b.Take(16).All(x => x == 0)) return true;                                         // ::
        if (b[0] == 0x01 && b.Skip(1).Take(7).All(x => x == 0)) return true;                  // 100::/64
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return true;        // 2001:db8::/32
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return true;        // 2001:0::/32 Teredo
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x01) return true;        // 2001:1::/32 speciale anycast-toewijzingen
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01)
            return true;                                                                      // 64:ff9b:1::/48

        // Binnen 2000::/3: alle IANA IPv6 Special-Purpose-toewijzingen met Globally Reachable = False.
        // Globally Reachable = True (bewust NIET geblokkeerd): 2001:3::/32 AMT, 2001:4:112::/48 en 2620:4f:8000::/48 AS112.
        foreach (var (prefix, bits) in BeleidsmatigGeblokkeerdBinnenGlobaalUnicast)
            if (HeeftPrefix(b, prefix, bits)) return true;
        // 5f00::/16 (SRv6 SIDs, RFC 9602) ligt buiten 2000::/3 en is al geblokkeerd door de 2000::/3-poort.
        return false;
    }

    /// <summary>
    /// Haalt het ingebedde IPv4-adres uit IPv4-compatible (::a.b.c.d), IPv4-translated
    /// (::ffff:0:a.b.c.d), NAT64 (64:ff9b::/96) en 6to4 (2002::/16).
    /// </summary>
    private static bool TryIngebeddeIpv4(byte[] b, out IPAddress ipv4)
    {
        ipv4 = IPAddress.None;
        var eerste8Nul = b.Take(8).All(x => x == 0);

        // ::a.b.c.d (IPv4-compatible): eerste 12 bytes nul.
        var compatible = eerste8Nul && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0;
        // ::ffff:0:a.b.c.d (IPv4-translated).
        var translated = eerste8Nul && b[8] == 0xFF && b[9] == 0xFF && b[10] == 0 && b[11] == 0;
        // 64:ff9b::/96 (NAT64).
        var nat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B
                    && b.Skip(4).Take(8).All(x => x == 0);

        if (compatible || translated || nat64)
        {
            ipv4 = new IPAddress(new[] { b[12], b[13], b[14], b[15] });
            return true;
        }

        // 2002::/16 (6to4): IPv4 in bytes 2-5.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            ipv4 = new IPAddress(new[] { b[2], b[3], b[4], b[5] });
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolvet <paramref name="host"/> (of parset het als IP-literal) en geeft het eerste
    /// publieke adres terug, of <c>null</c> als geen enkel geretourneerd adres publiek is.
    /// <paramref name="resolver"/> is een testbare seam — productiecode laat dit weg en gebruikt
    /// dan <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.
    /// </summary>
    public static async Task<IPAddress?> ResolveAllowedAddressAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal))
            return IsPublicIpAddress(literal) ? literal : null;

        resolver ??= static (h, c) => Dns.GetHostAddressesAsync(h, c);
        IPAddress[] addresses;
        try
        {
            addresses = await resolver(host, ct);
        }
        catch
        {
            return null;
        }

        return Array.Find(addresses, IsPublicIpAddress);
    }

    /// <summary>
    /// De feitelijke connectielogica achter <see cref="CreateHttpClient"/>'s <c>ConnectCallback</c>.
    /// Internal + testbaar via <c>InternalsVisibleTo</c>: <paramref name="resolver"/> en
    /// <paramref name="isAllowedEndpointOverride"/> laten tests DNS en het publiek/privé-oordeel
    /// simuleren (o.a. een "DNS-wijziging" tussen twee resoluties) zonder echte DNS of externe
    /// hosts; <paramref name="socketConnector"/> laat tests de transportlaag naar een lokale
    /// loopback-listener omleiden terwijl de validatie een ander (test-)adres beoordeelt;
    /// <paramref name="isAllowedPortOverride"/> laat tests een lokale listener op een
    /// niet-standaardpoort gebruiken. Productiecode (<see cref="CreateHttpClient"/> zonder
    /// overrides) gebruikt altijd de echte <see cref="Dns"/>-resolutie, <see cref="IsPublicIpAddress"/>,
    /// <see cref="IsAllowedPort"/> en een echte <see cref="Socket"/>-verbinding.
    /// </summary>
    internal static async ValueTask<Stream> ConnectCoreAsync(
        string host,
        int port,
        CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        Func<IPAddress, int, bool>? isAllowedEndpointOverride = null,
        Func<IPAddress, int, CancellationToken, Task<Socket>>? socketConnector = null,
        Func<int, bool>? isAllowedPortOverride = null)
    {
        var isAllowedPort = isAllowedPortOverride ?? IsAllowedPort;
        if (!isAllowedPort(port))
            throw new SsrfBlockedException($"Poort {port} is niet toegestaan.");

        IPAddress? target;
        if (isAllowedEndpointOverride != null)
        {
            IPAddress[] candidates = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await (resolver ?? ((h, c) => Dns.GetHostAddressesAsync(h, c)))(host, ct);
            target = Array.Find(candidates, a => isAllowedEndpointOverride(a, port));
        }
        else
        {
            target = await ResolveAllowedAddressAsync(host, resolver, ct);
        }

        if (target == null)
            throw new SsrfBlockedException($"Host '{host}' resolveert niet naar een toegestaan adres.");

        if (socketConnector != null)
        {
            var testSocket = await socketConnector(target, port, ct);
            return new NetworkStream(testSocket, ownsSocket: true);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(target, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// De <c>Accept</c>-header die elke via <see cref="CreateHttpClient"/> gebouwde client
    /// meestuurt. Zie <see cref="DefaultAcceptLanguage"/> voor de reden dat deze bestaat.
    /// </summary>
    internal const string DefaultAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    /// <summary>
    /// De <c>Accept-Language</c>-header die elke via <see cref="CreateHttpClient"/> gebouwde client
    /// meestuurt.
    /// <para>
    /// Reden: een verzoek met uitsluitend een <c>User-Agent</c> wordt door sommige hosts van
    /// clubwebsites met <c>403 Forbidden</c> beantwoord, terwijl hetzelfde verzoek mét deze twee
    /// headers gewoon <c>200 OK</c> geeft. Dat is kale header-sniffing op een browser-achtig
    /// verzoekprofiel, geen bot-bescherming die we omzeilen: de aanvraag zelf is en blijft een
    /// gewone, door de beheerder geïnitieerde GET op de eigen, in <c>AppSettings</c> vastgelegde
    /// clubwebsite. Zonder deze headers faalt de thema-extractor op zulke hosts zonder dat er
    /// iets mis is.
    /// </para>
    /// </summary>
    internal const string DefaultAcceptLanguage = "nl-NL,nl;q=0.9,en;q=0.8";

    /// <summary>
    /// De handler achter <see cref="CreateHttpClient"/>: geen automatische redirects, geen
    /// systeemproxy en geen automatische decompressie (de gelezen bytes zijn dus de bytes op de lijn,
    /// waarop <see cref="ReadBoundedStringAsync"/> begrenst).
    /// </summary>
    internal static SocketsHttpHandler CreateHandler(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolverOverride = null,
        Func<IPAddress, int, bool>? isAllowedEndpointOverride = null,
        Func<int, bool>? isAllowedPortOverride = null) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false, // geen omweg langs een omgevings-/systeemproxy: de ConnectCallback valideert het echte doeladres
        AutomaticDecompression = DecompressionMethods.None,
        ConnectCallback = (context, ct) => ConnectCoreAsync(
            context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct,
            resolverOverride, isAllowedEndpointOverride, null, isAllowedPortOverride)
    };

    /// <summary>
    /// Bouwt een <see cref="HttpClient"/> met redirects uit en een <c>ConnectCallback</c> die elke
    /// verbinding (initieel én elke handmatig gevolgde redirect-hop) resolvet en valideert vlak
    /// vóór het openen van de TCP-verbinding. De optionele overrides zijn uitsluitend voor tests —
    /// productiecode roept dit altijd zonder overrides aan.
    /// </summary>
    public static HttpClient CreateHttpClient(
        TimeSpan? timeout = null,
        string userAgent = "SportlinkAdmin/2.0",
        Func<string, CancellationToken, Task<IPAddress[]>>? resolverOverride = null,
        Func<IPAddress, int, bool>? isAllowedEndpointOverride = null,
        Func<int, bool>? isAllowedPortOverride = null)
    {
        var handler = CreateHandler(resolverOverride, isAllowedEndpointOverride, isAllowedPortOverride);
        var client = new HttpClient(handler) {
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        client.DefaultRequestHeaders.Add("User-Agent", userAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(DefaultAccept);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(DefaultAcceptLanguage);
        return client;
    }

    /// <summary>
    /// Volgt redirects handmatig, begrensd tot <paramref name="maxRedirects"/> hops, en valideert
    /// schema/poort op élke hop vóór verzending. De IP-validatie (publiek vs. privé) gebeurt niet
    /// hier maar in de <c>ConnectCallback</c> van de meegegeven client — dus ook op elke hop,
    /// gebonden aan de daadwerkelijke connectie. <paramref name="isAllowedPortOverride"/> is
    /// uitsluitend voor tests (zie <see cref="TryValidateUriShape"/>).
    /// <para>
    /// <paramref name="isAllowedHop"/> (optioneel) toetst elke bestemming, de start-URL en elke
    /// redirect-hop, opnieuw aan het beleid van de aanroeper (bijv. een host-allowlist). Een
    /// redirect van https naar http wordt altijd geweigerd. Antwoorden worden na de headers
    /// teruggegeven (<see cref="HttpCompletionOption.ResponseHeadersRead"/>); lees de body met
    /// <see cref="ReadBoundedStringAsync"/>.
    /// </para>
    /// </summary>
    public static async Task<HttpResponseMessage> GetWithBoundedRedirectsAsync(
        HttpClient client, Uri startUri, int maxRedirects = MaxRedirects,
        Func<int, bool>? isAllowedPortOverride = null,
        Func<Uri, bool>? isAllowedHop = null,
        CancellationToken ct = default)
    {
        var current = startUri;
        for (var hop = 0; hop <= maxRedirects; hop++)
        {
            if (!TryValidateUriShape(current, out var shapeError, isAllowedPortOverride))
                throw new SsrfBlockedException(shapeError!);
            if (isAllowedHop != null && !isAllowedHop(current))
                throw new SsrfBlockedException("Redirect-bestemming is niet toegestaan.");

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (ex.InnerException is SsrfBlockedException blocked)
            {
                // SocketsHttpHandler wikkelt elke uitzondering uit de ConnectCallback in een
                // HttpRequestException. Hier weer uitpakken zodat callers altijd consistent
                // SsrfBlockedException krijgen, ongeacht of de weigering plaatsvond bij de
                // URI-vormcontrole hierboven of pas bij de daadwerkelijke connectiepoging.
                throw blocked;
            }

            if (IsRedirectStatus(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null)
                    throw new SsrfBlockedException("Redirect zonder Location-header.");
                var volgende = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (current.Scheme == Uri.UriSchemeHttps && volgende.Scheme != Uri.UriSchemeHttps)
                    throw new SsrfBlockedException("Redirect naar een onveilig schema is niet toegestaan.");
                current = volgende;
                continue;
            }
            return response;
        }
        throw new SsrfBlockedException("Te veel redirects.");
    }

    /// <summary>
    /// Leest de body als tekst en stopt bij <paramref name="maxBytes"/>: een groter antwoord geeft
    /// een <see cref="SsrfBlockedException"/> in plaats van volledig in het geheugen te komen.
    /// </summary>
    public static async Task<string> ReadBoundedStringAsync(
        HttpContent content, int maxBytes = MaxResponseBytes, CancellationToken ct = default,
        ResponseBudget? budget = null)
    {
        if (budget != null) maxBytes = Math.Min(maxBytes, budget.Remaining);
        if (content.Headers.ContentLength is { } lengte && lengte > maxBytes)
            throw new SsrfBlockedException("Antwoord is te groot.");

        using var bron = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var blok = new byte[8192];
        int gelezen;
        while ((gelezen = await bron.ReadAsync(blok, ct)) > 0)
        {
            if (buffer.Length + gelezen > maxBytes)
                throw new SsrfBlockedException("Antwoord is te groot.");
            buffer.Write(blok, 0, gelezen);
        }
        budget?.Consume((int)buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static bool IsRedirectStatus(HttpStatusCode code) => code is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}
