using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using SportlinkFunction.Infrastructure;
using Xunit;

namespace FunctionApp.Tests.Infrastructure;

/// <summary>
/// Regressietests voor #1007 — SSRF-bescherming van de thema-extractor
/// (<c>SportlinkFunction.Admin.AdminThemeFunction.Extract</c>). Twee aanvalspaden zijn
/// afgedekt: (1) een redirect naar een niet-toegestane bestemming, (2) een directe verbinding met
/// een privé/loopback/link-local adres. Beide worden hier getest op het niveau van
/// <see cref="SsrfProtection"/> zelf, zonder een echte externe host aan te roepen — voor de
/// redirect-scenario's met twee echte lokale loopback-listeners, zoals de componentproef in het
/// issue deed.
/// </summary>
public class SsrfProtectionTests
{
    // ---------------------------------------------------------------------------------------
    // IsPublicIpAddress — pure policy, geen netwerk nodig.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1")]        // loopback
    [InlineData("127.5.5.5")]        // loopback-bereik, niet alleen .1
    [InlineData("10.0.0.1")]         // RFC1918
    [InlineData("172.16.0.1")]       // RFC1918
    [InlineData("172.31.255.255")]   // RFC1918 bovengrens
    [InlineData("192.168.1.1")]      // RFC1918
    [InlineData("169.254.1.1")]      // link-local
    [InlineData("100.64.0.1")]       // CGNAT
    [InlineData("0.0.0.0")]          // "this network"
    [InlineData("255.255.255.255")]  // broadcast
    [InlineData("224.0.0.1")]        // multicast
    public void IsPublicIpAddress_PriveEnGereserveerdeIPv4Adressen_IsFalse(string ip)
    {
        SsrfProtection.IsPublicIpAddress(IPAddress.Parse(ip)).Should().BeFalse($"{ip} is geen publiek adres");
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("203.0.113.10")]
    public void IsPublicIpAddress_EchtPubliekIPv4Adres_IsTrue(string ip)
    {
        SsrfProtection.IsPublicIpAddress(IPAddress.Parse(ip)).Should().BeTrue($"{ip} is een publiek adres");
    }

    [Theory]
    [InlineData("::1")]              // loopback
    [InlineData("fe80::1")]          // link-local
    [InlineData("fc00::1")]          // unique local
    [InlineData("fd12:3456:789a::1")] // unique local
    public void IsPublicIpAddress_PriveEnGereserveerdeIPv6Adressen_IsFalse(string ip)
    {
        SsrfProtection.IsPublicIpAddress(IPAddress.Parse(ip)).Should().BeFalse($"{ip} is geen publiek adres");
    }

    [Fact]
    public void IsPublicIpAddress_Ipv4MappedIpv6VanPriveAdres_IsFalse()
    {
        // ::ffff:10.0.0.1 — IPv4-mapped IPv6-equivalent van een RFC1918-adres. Moet eerst
        // uitgepakt worden naar IPv4 en dan als privé herkend worden.
        var address = IPAddress.Parse("::ffff:10.0.0.1");
        SsrfProtection.IsPublicIpAddress(address).Should().BeFalse();
    }

    [Fact]
    public void IsPublicIpAddress_Ipv4MappedIpv6VanPubliekAdres_IsTrue()
    {
        var address = IPAddress.Parse("::ffff:8.8.8.8");
        SsrfProtection.IsPublicIpAddress(address).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // Poort- en schemavalidatie ("ongewenste poort").
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(80, true)]
    [InlineData(443, true)]
    [InlineData(22, false)]
    [InlineData(8080, false)]
    [InlineData(6379, false)]
    [InlineData(3389, false)]
    public void IsAllowedPort_StandaardBeleid(int port, bool verwacht)
    {
        SsrfProtection.IsAllowedPort(port).Should().Be(verwacht);
    }

    [Fact]
    public void TryValidateUriShape_OngewenstePoort_IsGeblokkeerd()
    {
        var uri = new Uri("http://club-website.example:8080/");
        SsrfProtection.TryValidateUriShape(uri, out var error).Should().BeFalse();
        error.Should().Contain("8080");
    }

    [Theory]
    [InlineData("ftp://club-website.example/")]
    [InlineData("file:///etc/passwd")]
    public void TryValidateUriShape_NietHttpOfHttpsSchema_IsGeblokkeerd(string url)
    {
        var uri = new Uri(url);
        SsrfProtection.TryValidateUriShape(uri, out var error).Should().BeFalse();
        error.Should().Contain("http");
    }

    [Fact]
    public void TryValidateUriShape_GeldigePubliekeHttpsUrl_IsToegestaan()
    {
        var uri = new Uri("https://club-website.example/");
        SsrfProtection.TryValidateUriShape(uri, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------
    // ResolveAllowedAddressAsync — DNS-resolutie met een injecteerbare resolver, zodat "een
    // hostnaam die naar een intern adres resolvet" gesimuleerd kan worden zonder echte DNS.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ResolveAllowedAddressAsync_HostResolveertUitsluitendNaarPriveAdressen_GeeftNullTerug()
    {
        var resolver = (string _, CancellationToken _) =>
            Task.FromResult(new[] { IPAddress.Parse("10.0.0.5"), IPAddress.Parse("127.0.0.1") });

        var result = await SsrfProtection.ResolveAllowedAddressAsync("interne-dienst.local", resolver);
        result.Should().BeNull("geen van de geretourneerde adressen is publiek");
    }

    [Fact]
    public async Task ResolveAllowedAddressAsync_HostResolveertNaarPubliekAdres_GeeftDatAdresTerug()
    {
        var publicAddress = IPAddress.Parse("93.184.216.34");
        var resolver = (string _, CancellationToken _) => Task.FromResult(new[] { publicAddress });

        var result = await SsrfProtection.ResolveAllowedAddressAsync("club-website.example", resolver);
        result.Should().Be(publicAddress);
    }

    [Fact]
    public async Task ResolveAllowedAddressAsync_IpLiteralDieLoopbackIs_GeeftNullTerug()
    {
        // Host is zelf al een IP-literal — geen resolver nodig, en loopback moet alsnog geweigerd worden.
        var result = await SsrfProtection.ResolveAllowedAddressAsync("127.0.0.1");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAllowedAddressAsync_GesimuleerdeDnsWijzigingTussenTweeAanroepen_TweedeAanroepBlokkeertAlsnog()
    {
        // Simuleert DNS-rebinding: eerst levert de "attacker"-DNS een publiek adres (zou een
        // vroegtijdige, losstaande validatie laten slagen), maar een DAADWERKELIJKE tweede
        // resolutie — zoals ConnectCoreAsync die zelf, vlak vóór verbinden, opnieuw uitvoert —
        // levert nu een privé-adres. Elke los-van-elkaar staande aanroep van
        // ResolveAllowedAddressAsync moet zelfstandig correct oordelen over wat hij toevallig
        // opgeleverd krijgt; de architecturale garantie tegen rebinding zit in ConnectCoreAsync
        // (één resolutie, direct gebruikt om te verbinden — zie de aparte test hieronder).
        var callCount = 0;
        var resolver = (string _, CancellationToken _) =>
        {
            callCount++;
            var addresses = callCount == 1
                ? new[] { IPAddress.Parse("93.184.216.34") }   // publiek — "vóór" de DNS-wijziging
                : new[] { IPAddress.Parse("10.0.0.9") };        // privé — "na" de DNS-wijziging
            return Task.FromResult(addresses);
        };

        var eersteResolutie = await SsrfProtection.ResolveAllowedAddressAsync("rebinding.example", resolver);
        var tweedeResolutie = await SsrfProtection.ResolveAllowedAddressAsync("rebinding.example", resolver);

        eersteResolutie.Should().NotBeNull();
        tweedeResolutie.Should().BeNull("de tweede resolutie levert nu een privé-adres op en moet geweigerd worden");
    }

    // ---------------------------------------------------------------------------------------
    // ConnectCoreAsync — de laag achter de ConnectCallback van SocketsHttpHandler. Bewijst dat
    // resolutie en validatie precies één keer gebeuren en direct gebruikt worden om te verbinden
    // (geen tweede, losse resolutie tussen check en connect — dat elimineert DNS-rebinding).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ConnectCoreAsync_HostResolveertNaarLoopback_GooitSsrfBlockedException()
    {
        var resolver = (string _, CancellationToken _) => Task.FromResult(new[] { IPAddress.Loopback });

        Func<Task> act = () => SsrfProtection.ConnectCoreAsync(
            "vermomde-interne-dienst.example", 443, CancellationToken.None, resolver).AsTask();

        await act.Should().ThrowAsync<SsrfBlockedException>();
    }

    [Fact]
    public async Task ConnectCoreAsync_HostResolveertNaarPriveAdres_GooitSsrfBlockedException()
    {
        var resolver = (string _, CancellationToken _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.50") });

        Func<Task> act = () => SsrfProtection.ConnectCoreAsync(
            "interne-dienst.example", 443, CancellationToken.None, resolver).AsTask();

        await act.Should().ThrowAsync<SsrfBlockedException>();
    }

    [Fact]
    public async Task ConnectCoreAsync_OngewenstePoort_GooitSsrfBlockedExceptionVoorDnsWordtGeraadpleegd()
    {
        var resolverAangeroepen = false;
        var resolver = (string _, CancellationToken _) =>
        {
            resolverAangeroepen = true;
            return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
        };

        Func<Task> act = () => SsrfProtection.ConnectCoreAsync(
            "club-website.example", 6379, CancellationToken.None, resolver).AsTask();

        await act.Should().ThrowAsync<SsrfBlockedException>();
        resolverAangeroepen.Should().BeFalse("de poortcontrole gebeurt vóór enige DNS-resolutie");
    }

    [Fact]
    public async Task ConnectCoreAsync_ResolverWordtPreciesEenKeerAangeroepen()
    {
        // Kern van de anti-rebinding-garantie: er is precies één resolutiemoment, dat direct
        // gebruikt wordt om te verbinden. Geen "check nu, resolve opnieuw bij connect" gat.
        var callCount = 0;
        var resolver = (string _, CancellationToken _) =>
        {
            callCount++;
            return Task.FromResult(new[] { IPAddress.Loopback }); // blokkeert, maar telt de aanroep
        };

        Func<Task> act = () => SsrfProtection.ConnectCoreAsync(
            "host.example", 443, CancellationToken.None, resolver).AsTask();

        await act.Should().ThrowAsync<SsrfBlockedException>();
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task ConnectCoreAsync_ValidatieSlaagtEnListenerNeemtDeConnectieAan_GeeftVerbondenStreamTerug()
    {
        // "Geldige, publieke clubwebsite blijft werken": met een resolver+override die het
        // testadres als toegestaan aanmerkt, moet ConnectCoreAsync daadwerkelijk verbinden met
        // een echte lokale listener — bewijst dat de happy path niet stukgaat door de SSRF-check.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();

        var resolver = (string _, CancellationToken _) => Task.FromResult(new[] { IPAddress.Loopback });

        await using var stream = await SsrfProtection.ConnectCoreAsync(
            "loopback-testhost.example", port, CancellationToken.None,
            resolver, isAllowedEndpointOverride: (_, _) => true, isAllowedPortOverride: _ => true);

        stream.Should().NotBeNull();
        var serverSocket = await acceptTask;
        serverSocket.Connected.Should().BeTrue();
        serverSocket.Dispose();
    }

    // ---------------------------------------------------------------------------------------
    // GetWithBoundedRedirectsAsync — de begrensde, handmatige redirect-lus. Reproduceert de
    // componentproef uit het issue met twee echte lokale loopback-listeners: de eerste stuurt een
    // 302 naar de tweede. Hier via een fake HttpMessageHandler voor de structurele lus-logica
    // (aantal hops, relatieve Location, stoppen bij niet-redirect), en apart via echte listeners
    // voor het end-to-end bewijs dat elke hop opnieuw gevalideerd wordt.
    // ---------------------------------------------------------------------------------------

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<Uri> RequestedUris { get; } = new();

        public StubHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) }
    };

    [Fact]
    public async Task GetWithBoundedRedirectsAsync_GeenRedirect_GeeftAntwoordDirectTerug()
    {
        using var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        using var client = new HttpClient(handler);

        using var response = await SsrfProtection.GetWithBoundedRedirectsAsync(client, new Uri("https://club-website.example/"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.RequestedUris.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetWithBoundedRedirectsAsync_VolgtRelatieveRedirectNaarAbsoluteUri()
    {
        using var handler = new StubHandler(
            Redirect("/nieuwe-pagina"),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        using var client = new HttpClient(handler);

        using var response = await SsrfProtection.GetWithBoundedRedirectsAsync(client, new Uri("https://club-website.example/oud"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.RequestedUris.Should().HaveCount(2);
        handler.RequestedUris[1].Should().Be(new Uri("https://club-website.example/nieuwe-pagina"));
    }

    [Fact]
    public async Task GetWithBoundedRedirectsAsync_TeVeelRedirects_GooitSsrfBlockedException()
    {
        var responses = Enumerable.Range(0, SsrfProtection.MaxRedirects + 2)
            .Select(_ => Redirect("https://club-website.example/volgende"))
            .ToArray();
        using var handler = new StubHandler(responses);
        using var client = new HttpClient(handler);

        Func<Task> act = () => SsrfProtection.GetWithBoundedRedirectsAsync(client, new Uri("https://club-website.example/start"));

        await act.Should().ThrowAsync<SsrfBlockedException>().WithMessage("*redirects*");
    }

    [Fact]
    public async Task GetWithBoundedRedirectsAsync_RedirectNaarOngewenstePoort_GooitSsrfBlockedException()
    {
        using var handler = new StubHandler(Redirect("http://club-website.example:8080/"));
        using var client = new HttpClient(handler);

        Func<Task> act = () => SsrfProtection.GetWithBoundedRedirectsAsync(client, new Uri("https://club-website.example/start"));

        await act.Should().ThrowAsync<SsrfBlockedException>();
    }

    [Fact]
    public async Task GetWithBoundedRedirectsAsync_EchteLoopbackListenersRedirectNaarAndereListener_TweedeHopWordtOpnieuwGevalideerdEnGeblokkeerd()
    {
        // Reproduceert de componentproef uit issue #1007: twee echte lokale loopback-listeners,
        // de eerste antwoordt met een 302 naar de tweede. isAllowedPortOverride laat de niet-
        // standaardpoorten van de testlisteners toe (productiecode gebruikt dit nooit); de
        // ConnectCallback-override merkt uitsluitend de poort van listener A als toegestaan aan
        // — exact zoals een aanvaller een toegestane host zou misbruiken om via een redirect een
        // andere, niet-toegestane bestemming (listener B) te bereiken. Het bewijs dat de fix werkt:
        // de tweede hop wordt alsnog geblokkeerd, ondanks dat de eerste hop legitiem slaagde.
        using var listenerA = new TcpListener(IPAddress.Loopback, 0);
        listenerA.Start();
        var portA = ((IPEndPoint)listenerA.LocalEndpoint).Port;

        using var listenerB = new TcpListener(IPAddress.Loopback, 0);
        listenerB.Start();
        var portB = ((IPEndPoint)listenerB.LocalEndpoint).Port;

        var serveOneRedirect = ServeSingleHttpResponseAsync(listenerA,
            $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{portB}/intern\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        using var client = SsrfProtection.CreateHttpClient(
            isAllowedEndpointOverride: (_, port) => port == portA, // alléén listener A is "toegestaan"
            isAllowedPortOverride: _ => true);

        Func<Task> act = () => SsrfProtection.GetWithBoundedRedirectsAsync(
            client, new Uri($"http://127.0.0.1:{portA}/"), isAllowedPortOverride: _ => true);

        await act.Should().ThrowAsync<SsrfBlockedException>(
            "listener B representeert de niet-toegestane bestemming waar de redirect naartoe wijst");

        await serveOneRedirect;
        listenerB.Stop();
        listenerA.Stop();
    }

    [Fact]
    public async Task GetWithBoundedRedirectsAsync_DirecteVerbindingMetNietToegestaanAdres_WordtGeblokkeerd()
    {
        // "direct-interne-host": geen redirect, alleen een directe aanroep naar een adres dat
        // niet op de (test-)allowlist staat.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = SsrfProtection.CreateHttpClient(
            isAllowedEndpointOverride: (_, p) => p != port, // dit specifieke adres is juist NIET toegestaan
            isAllowedPortOverride: _ => true);

        Func<Task> act = () => SsrfProtection.GetWithBoundedRedirectsAsync(
            client, new Uri($"http://127.0.0.1:{port}/"), isAllowedPortOverride: _ => true);

        await act.Should().ThrowAsync<SsrfBlockedException>();
        listener.Stop();
    }

    [Fact]
    public async Task GetWithBoundedRedirectsAsync_GeldigeToegestaneBestemming_BlijftWerken()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serveOk = ServeSingleHttpResponseAsync(listener,
            "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 13\r\nConnection: close\r\n\r\n<html></html>");

        using var client = SsrfProtection.CreateHttpClient(
            isAllowedEndpointOverride: (_, _) => true, isAllowedPortOverride: _ => true);

        using var response = await SsrfProtection.GetWithBoundedRedirectsAsync(
            client, new Uri($"http://127.0.0.1:{port}/"), isAllowedPortOverride: _ => true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("<html></html>");
        await serveOk;
        listener.Stop();
    }

    /// <summary>Accepteert precies één TCP-verbinding op <paramref name="listener"/> en schrijft er de rauwe HTTP-response op.</summary>
    private static async Task ServeSingleHttpResponseAsync(TcpListener listener, string rawHttpResponse)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var networkStream = client.GetStream();
        // Wacht tot het verzoek binnen is (we hoeven het niet te parsen voor deze test).
        var buffer = new byte[4096];
        await networkStream.ReadAsync(buffer);
        var bytes = System.Text.Encoding.ASCII.GetBytes(rawHttpResponse);
        await networkStream.WriteAsync(bytes);
        await networkStream.FlushAsync();
    }
}
