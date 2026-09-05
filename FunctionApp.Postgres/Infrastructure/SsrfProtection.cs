using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace FunctionApp.Postgres.Infrastructure;

/// <summary>
/// Gooit deze uitzondering als een uitgaande HTTP-bestemming door <see cref="SsrfProtection"/>
/// geweigerd wordt (privé/loopback/link-local IP, ongewenste poort of te veel redirects).
/// </summary>
public sealed class SsrfBlockedException : Exception
{
    public SsrfBlockedException(string message) : base(message) { }
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
    /// </summary>
    public static bool IsPublicIpAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0) return false;                                   // 0.0.0.0/8
            if (b[0] == 10) return false;                                  // 10.0.0.0/8
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;     // 100.64.0.0/10 CGNAT
            if (b[0] == 127) return false;                                 // 127.0.0.0/8 loopback
            if (b[0] == 169 && b[1] == 254) return false;                  // 169.254.0.0/16 link-local
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;     // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;       // 192.0.0.0/24 IETF
            if (b[0] == 192 && b[1] == 168) return false;                  // 192.168.0.0/16
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;   // 198.18.0.0/15 benchmark
            if (b[0] >= 224) return false;                                 // multicast/gereserveerd/broadcast
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                return false;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false; // fc00::/7 unique local
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
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = (context, ct) => ConnectCoreAsync(
                context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct,
                resolverOverride, isAllowedEndpointOverride, null, isAllowedPortOverride)
        };
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("User-Agent", userAgent);
        return client;
    }

    /// <summary>
    /// Volgt redirects handmatig, begrensd tot <paramref name="maxRedirects"/> hops, en valideert
    /// schema/poort op élke hop vóór verzending. De IP-validatie (publiek vs. privé) gebeurt niet
    /// hier maar in de <c>ConnectCallback</c> van de meegegeven client — dus ook op elke hop,
    /// gebonden aan de daadwerkelijke connectie. <paramref name="isAllowedPortOverride"/> is
    /// uitsluitend voor tests (zie <see cref="TryValidateUriShape"/>).
    /// </summary>
    public static async Task<HttpResponseMessage> GetWithBoundedRedirectsAsync(
        HttpClient client, Uri startUri, int maxRedirects = MaxRedirects,
        Func<int, bool>? isAllowedPortOverride = null)
    {
        var current = startUri;
        for (var hop = 0; hop <= maxRedirects; hop++)
        {
            if (!TryValidateUriShape(current, out var shapeError, isAllowedPortOverride))
                throw new SsrfBlockedException(shapeError!);

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(current);
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
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            return response;
        }
        throw new SsrfBlockedException("Te veel redirects.");
    }

    private static bool IsRedirectStatus(HttpStatusCode code) => code is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}
