using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

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

    // #1622 — aanscherping adresbereikcontrole.

    [Theory]
    [InlineData("::")]                                     // unspecified
    [InlineData("::a00:1")]                                // IPv4-compatible, ingebed 10.0.0.1
    [InlineData("::7f00:1")]                               // IPv4-compatible, ingebed loopback
    [InlineData("::c0a8:101")]                             // IPv4-compatible, ingebed 192.168.1.1
    [InlineData("::ffff:0:a00:1")]                         // IPv4-translated, ingebed 10.0.0.1
    [InlineData("::ffff:0:a9fe:a9fe")]                     // IPv4-translated, ingebed link-local
    [InlineData("64:ff9b::a00:1")]                         // NAT64, ingebed 10.0.0.1
    [InlineData("64:ff9b::7f00:1")]                        // NAT64, ingebed loopback
    [InlineData("64:ff9b::a9fe:a9fe")]                     // NAT64, ingebed link-local
    [InlineData("64:ff9b:1::a00:1")]                       // NAT64 lokaal gebruik
    [InlineData("64:ff9b:1::808:808")]                     // NAT64 lokaal gebruik, volledig geblokkeerd
    [InlineData("2002:a00:1::1")]                          // 6to4, ingebed 10.0.0.1
    [InlineData("2002:7f00:1::1")]                         // 6to4, ingebed loopback
    [InlineData("2002:a9fe:a9fe::1")]                      // 6to4, ingebed link-local
    [InlineData("2001::1")]                                // Teredo
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]   // Teredo, publieke server en client
    [InlineData("2001:0:a00:1::1")]                        // Teredo, privé serveradres
    [InlineData("2001:db8::1")]                            // documentatie
    [InlineData("2001:db8:ffff::1")]                       // documentatie, bovenkant
    [InlineData("100::1")]                                 // discard-only
    [InlineData("100::ffff:ffff:ffff:ffff")]               // discard-only, bovenkant
    [InlineData("168.63.129.16")]                          // Azure-platformadres
    public void IsPublicIpAddress_AangescherpteBereiken_IsFalse(string ip)
    {
        SsrfProtection.IsPublicIpAddress(IPAddress.Parse(ip)).Should().BeFalse($"{ip} is geen publiek adres");
    }

    [Theory]
    [InlineData("2606:4700::1111")]
    [InlineData("2a00:1450:4001::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2002:808:808::1")]                        // 6to4, publiek ingebed adres
    [InlineData("2001:4860:4860::8888")]                   // gewoon publiek adres net boven Teredo
    [InlineData("2001:db9::1")]                            // net buiten documentatieprefix
    [InlineData("168.63.129.15")]                          // buren van het Azure-platformadres
    [InlineData("168.63.129.17")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("169.253.0.1")]
    [InlineData("198.17.0.1")]
    [InlineData("198.20.0.1")]
    [InlineData("192.169.0.1")]
    public void IsPublicIpAddress_GrensnegatievenEnPubliekeAdressen_IsTrue(string ip)
    {
        SsrfProtection.IsPublicIpAddress(IPAddress.Parse(ip)).Should().BeTrue($"{ip} is een publiek adres");
    }

    // #1622 review ronde 1 — grenswaarden per prefix: begin, einde en direct aangrenzende waarde.
    // Een fout in prefixlengte of bytepositie laat minstens één van deze gevallen falen.

    [Theory]
    [InlineData("100::")]                                              // 100::/64 begin
    [InlineData("2001:db8::")]                                         // 2001:db8::/32 begin
    [InlineData("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff")]             // einde
    [InlineData("2001::")]                                             // Teredo begin
    [InlineData("2001:0:ffff:ffff:ffff:ffff:ffff:ffff")]               // Teredo einde
    [InlineData("2001:1::")]                                           // 2001:1::/32 (speciale anycast-toewijzing) begin
    [InlineData("2001:1::1")]                                          // PCP-anycast: bewust geblokkeerd
    [InlineData("2001:1:ffff:ffff:ffff:ffff:ffff:ffff")]               // einde
    [InlineData("64:ff9b:1::")]                                        // 64:ff9b:1::/48 begin
    [InlineData("64:ff9b:1:ffff:ffff:ffff:ffff:ffff")]                 // einde
    [InlineData("fc00::")]                                             // fc00::/7 begin
    [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]            // einde
    [InlineData("64:ff9b::")]                                          // NAT64, ingebed 0.0.0.0
    [InlineData("64:ff9b::ffff:ffff")]                                 // NAT64, ingebed 255.255.255.255
    [InlineData("64:ff9b::a00:0")]                                     // NAT64, ingebed 10.0.0.0
    [InlineData("64:ff9b::aff:ffff")]                                  // NAT64, ingebed 10.255.255.255
    [InlineData("::a00:0")]                                            // compatible, 10.0.0.0
    [InlineData("::aff:ffff")]                                         // compatible, 10.255.255.255
    [InlineData("::ffff:0:aff:ffff")]                                  // translated, 10.255.255.255
    [InlineData("2002:a00:0:1::")]                                     // 6to4, 10.0.0.0
    [InlineData("2002:aff:ffff::1")]                                   // 6to4, 10.255.255.255
    [InlineData("2002:ac10:0::1")]                                     // 6to4, 172.16.0.0
    [InlineData("2002:ac1f:ffff::1")]                                  // 6to4, 172.31.255.255
    [InlineData("2002:6440:0::1")]                                     // 6to4, 100.64.0.0
    [InlineData("2002:647f:ffff::1")]                                  // 6to4, 100.127.255.255
    [InlineData("2002:c0a8:0::1")]                                     // 6to4, 192.168.0.0
    [InlineData("2002:c0a8:ffff::1")]                                  // 6to4, 192.168.255.255
    [InlineData("2002:a9fe:0::1")]                                     // 6to4, 169.254.0.0
    [InlineData("2002:0:0::1")]                                        // 6to4, 0.0.0.0
    [InlineData("2002:e000:0::1")]                                     // 6to4, 224.0.0.0
    public void IsPublicIpAddress_GrenzenAangescherptePrefixen_IsFalse(string ip)
    {
        SsrfProtection.IsPublicIpAddress(IPAddress.Parse(ip)).Should().BeFalse($"{ip} valt binnen een geblokkeerd bereik");
    }

    [Theory]
    [InlineData("2001:db7:ffff:ffff:ffff:ffff:ffff:ffff")]             // direct onder documentatie
    [InlineData("2001:db9::")]                                         // direct boven documentatie
    [InlineData("2000:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]            // direct onder Teredo
    [InlineData("2001:2:1::1")]                                        // direct boven 2001:1::/32
    [InlineData("2001:3::1")]                                          // verder boven 2001:1::/32
    [InlineData("2002:9ff:ffff::1")]                                   // 6to4, 9.255.255.255
    [InlineData("2002:b00:0::1")]                                      // 6to4, 11.0.0.0
    [InlineData("2002:ac0f:ffff::1")]                                  // 6to4, 172.15.255.255
    [InlineData("2002:ac20:0::1")]                                     // 6to4, 172.32.0.0
    [InlineData("2002:643f:ffff::1")]                                  // 6to4, 100.63.255.255
    [InlineData("2002:6480:0::1")]                                     // 6to4, 100.128.0.0
    [InlineData("2002:c0a7:ffff::1")]                                  // 6to4, 192.167.255.255
    [InlineData("2002:c0a9:0::1")]                                     // 6to4, 192.169.0.0
    [InlineData("2002:a9ff:0::1")]                                     // 6to4, 169.255.0.0
    [InlineData("2002:dfff:ffff::1")]                                  // 6to4, 223.255.255.255
    [InlineData("2001:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]            // direct onder 2002::/16
    [InlineData("2003::")]                                             // direct boven 2002::/16
    public void IsPublicIpAddress_GrenzenBuitenAangescherptePrefixen_IsTrue(string ip)
    {
        SsrfProtection.IsPublicIpAddress(IPAddress.Parse(ip)).Should().BeTrue($"{ip} ligt buiten alle geblokkeerde bereiken");
    }

    // #1622 review ronde 2 — beleid: een IPv6-adres kan alleen publiek zijn binnen het globale
    // unicastbereik 2000::/3 (RFC 4291). Deze gevallen stonden eerder als "publiek" en zijn hierheen
    // verplaatst: ze liggen buiten 2000::/3, niet globaal unicast, dus fail-closed geblokkeerd.
    [Theory]
    [InlineData("64:ff9b::808:808")]                       // NAT64, publiek ingebed adres
    [InlineData("::808:808")]                              // IPv4-compatible, publiek ingebed adres
    [InlineData("::ffff:0:808:808")]                       // IPv4-translated, publiek ingebed adres
    [InlineData("100:0:0:1::1")]                           // net buiten discard-prefix
    [InlineData("ff:ffff:ffff:ffff::")]                                // direct onder 100::/64 (byte 0 afwijkend)
    [InlineData("101::")]                                              // direct boven 100::/64 (byte 1 afwijkend)
    [InlineData("100:0:0:1::")]                                        // net buiten 100::/64
    [InlineData("64:ff9b:2::")]                                        // direct boven 64:ff9b:1::/48
    [InlineData("64:ff9b:0:1::")]                                      // geen NAT64-/96, geen lokaal /48
    [InlineData("fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]            // direct onder fc00::/7
    [InlineData("fe00::")]                                             // direct boven fc00::/7
    [InlineData("64:ff9b::9ff:ffff")]                                  // NAT64, 9.255.255.255
    [InlineData("64:ff9b::b00:0")]                                     // NAT64, 11.0.0.0
    [InlineData("64:ff9b:0:0:0:1:808:808")]                            // net buiten NAT64 /96
    [InlineData("::9ff:ffff")]                                         // compatible, 9.255.255.255
    [InlineData("::b00:0")]                                            // compatible, 11.0.0.0
    [InlineData("::1:a00:1")]                                          // geen compatible
    [InlineData("0:0:0:1::808:808")]                                   // geen compatible/translated
    [InlineData("::fffe:0:808:808")]                                   // geen translated
    [InlineData("::ffff:1:808:808")]                                   // geen translated
    [InlineData("::ffff:0:b00:0")]                                     // translated, 11.0.0.0
    public void IsPublicIpAddress_BuitenGlobaalUnicast2000Slash3_IsFalse(string ip)
    {
        SsrfProtection.IsPublicIpAddress(IPAddress.Parse(ip)).Should().BeFalse($"{ip} ligt buiten 2000::/3, niet globaal unicast");
    }

    // #1622 review ronde 2 — grenzen systematisch uit (prefix, lengte) berekend, niet met de hand gekozen.

    private static (IPAddress Eerste, IPAddress Laatste, IPAddress? Voorganger, IPAddress? Opvolger) Grenzen(string prefix, int lengte)
    {
        var basis = new System.Numerics.BigInteger(IPAddress.Parse(prefix).GetAddressBytes(), isUnsigned: true, isBigEndian: true);
        var grootte = System.Numerics.BigInteger.One << (128 - lengte);
        var eerste = basis & ~(grootte - 1);
        var laatste = eerste + grootte - 1;
        var max = (System.Numerics.BigInteger.One << 128) - 1;
        static IPAddress Naar(System.Numerics.BigInteger n)
        {
            var bytes = n.ToByteArray(isUnsigned: true, isBigEndian: true);
            var vol = new byte[16];
            Array.Copy(bytes, 0, vol, 16 - bytes.Length, bytes.Length);
            return new IPAddress(vol);
        }
        return (Naar(eerste), Naar(laatste),
            eerste.IsZero ? null : Naar(eerste - 1),
            laatste == max ? null : Naar(laatste + 1));
    }

    // Binnen 2000::/3: eerste en laatste geblokkeerd; voorganger/opvolger publiek, behalve waar de
    // buur zelf in een ander geblokkeerd bereik valt. 2001::/32 (Teredo) en 2001:1::/32 grenzen aan
    // elkaar: de opvolger van Teredo en de voorganger van 2001:1::/32 zijn dus geblokkeerd.
    // Buiten 2000::/3 (100::/64, 64:ff9b::/96, 64:ff9b:1::/48, fc00::/7): alles, ook de buren,
    // geblokkeerd door de 2000::/3-poort (64:ff9b:1::/48 en 64:ff9b::/96 liggen bovendien tegen elkaar).
    [Theory]
    [InlineData("2001:db8::", 32, true, true)]
    [InlineData("2001::", 32, true, false)]
    [InlineData("2001:1::", 32, false, false)]
    [InlineData("100::", 64, false, false)]
    [InlineData("64:ff9b::", 96, false, false)]
    [InlineData("64:ff9b:1::", 48, false, false)]
    [InlineData("fc00::", 7, false, false)]
    // IANA special-purpose (applicatiebeleid (#1622), geen IANA-classificatie) binnen 2000::/3. Aangrenzend geblokkeerd:
    // voorganger van 2001:2::/48 (2001:1:ffff:..) valt in 2001:1::/32; opvolger van 2001:10::/28 is 2001:20::,
    // van 2001:20::/28 is 2001:30:: (beide geblokkeerd). 2001:30::/28 -> 2001:40:: publiek.
    [InlineData("2001:2::", 48, false, true)]
    [InlineData("2001:10::", 28, true, false)]
    [InlineData("2001:20::", 28, false, false)]
    [InlineData("2001:30::", 28, false, true)]
    [InlineData("3fff::", 20, true, true)] // 3fff:0000::-3fff:0fff:: ; voorganger 3ffe:ffff:.. en opvolger 3fff:1000:: publiek
    [InlineData("5f00::", 16, false, false)] // buiten 2000::/3: ook de buren zijn geblokkeerd
    public void Prefixgrenzen_WordenExactBewaakt(string prefix, int lengte, bool voorgangerPubliek, bool opvolgerPubliek)
    {
        var (eerste, laatste, voorganger, opvolger) = Grenzen(prefix, lengte);

        SsrfProtection.IsPublicIpAddress(eerste).Should().BeFalse($"eerste adres {eerste} van {prefix}/{lengte}");
        SsrfProtection.IsPublicIpAddress(laatste).Should().BeFalse($"laatste adres {laatste} van {prefix}/{lengte}");
        SsrfProtection.IsPublicIpAddress(voorganger!).Should().Be(voorgangerPubliek, $"voorganger {voorganger} van {prefix}/{lengte}");
        SsrfProtection.IsPublicIpAddress(opvolger!).Should().Be(opvolgerPubliek, $"opvolger {opvolger} van {prefix}/{lengte}");
    }

    [Theory]
    [InlineData("1fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)] // direct onder 2000::/3
    [InlineData("2000::", true)]                                    // eerste adres 2000::/3
    [InlineData("3ffe:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]   // direct onder 3fff::/20
    [InlineData("3fff:1000::", true)]                               // direct boven 3fff::/20, nog binnen 2000::/3
    [InlineData("3fff::", false)]                                   // eerste adres 3fff::/20 documentatie (RFC 9637)
    [InlineData("3fff:fff:ffff:ffff:ffff:ffff:ffff:ffff", false)]   // laatste adres 3fff::/20
    [InlineData("3fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]   // laatste adres 2000::/3; ligt NIET in 3fff::/20 (die loopt tot 3fff:0fff:..)
    [InlineData("4000::", false)]                                   // direct boven 2000::/3
    public void GlobaalUnicastGrenzen_2000Slash3(string ip, bool verwachtPubliek)
    {
        SsrfProtection.IsPublicIpAddress(IPAddress.Parse(ip)).Should().Be(verwachtPubliek);
    }

    // IANA Special-Purpose Registry: Globally Reachable = True, dus bewust publiek (beleid in beide richtingen).
    [Theory]
    [InlineData("2001:3::1")]       // 2001:3::/32 AMT (RFC 7450)
    [InlineData("2001:4:112::1")]   // 2001:4:112::/48 AS112-v6 (RFC 7534)
    [InlineData("2620:4f:8000::1")] // 2620:4f:8000::/48 Direct Delegation AS112 (RFC 7534)
    public void GloballyReachableSpecialPurpose_BlijftPubliek(string ip)
    {
        SsrfProtection.IsPublicIpAddress(IPAddress.Parse(ip)).Should().BeTrue($"{ip} is in IANA Globally Reachable = True");
    }

    // 6to4 (2002::/16) met ingebed IPv4: laatste adres van elk privé-/gereserveerd bereik geblokkeerd,
    // het eerste adres erna publiek (uitgezonderd 224.0.0.0: multicast/gereserveerd).
    [Theory]
    [InlineData("10.255.255.255", false)]
    [InlineData("11.0.0.0", true)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.0", true)]
    [InlineData("192.168.255.255", false)]
    [InlineData("192.169.0.0", true)]
    [InlineData("100.127.255.255", false)]
    [InlineData("100.128.0.0", true)]
    [InlineData("169.254.255.255", false)]
    [InlineData("169.255.0.0", true)]
    [InlineData("198.19.255.255", false)]
    [InlineData("198.20.0.0", true)]
    [InlineData("192.0.0.255", false)]
    [InlineData("192.0.1.0", true)]
    [InlineData("127.255.255.255", false)]
    [InlineData("128.0.0.0", true)]
    [InlineData("0.255.255.255", false)]
    [InlineData("1.0.0.0", true)]
    [InlineData("223.255.255.255", true)]
    [InlineData("224.0.0.0", false)]
    public void Ipv4Grenzen_DirectEnIngebedIn6to4(string ipv4, bool verwachtPubliek)
    {
        var v4 = IPAddress.Parse(ipv4).GetAddressBytes();
        var zesNaarVier = new IPAddress(new byte[] { 0x20, 0x02, v4[0], v4[1], v4[2], v4[3], 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });

        SsrfProtection.IsPublicIpAddress(IPAddress.Parse(ipv4)).Should().Be(verwachtPubliek, $"direct IPv4 {ipv4}");
        SsrfProtection.IsPublicIpAddress(zesNaarVier).Should().Be(verwachtPubliek, $"6to4 met ingebed {ipv4}");
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

    // ---------------------------------------------------------------------------------------
    // CreateHttpClient — verzoekheaders (#1250, taak 1).
    //
    // Een verzoek met alléén een User-Agent wordt door sommige hosts van clubwebsites met 403
    // beantwoord; mét Accept + Accept-Language geeft exact hetzelfde verzoek 200. Deze tests zijn
    // het regressieslot daarop: geen live HTTP-call nodig, maar wél bewijs dat de headers
    // daadwerkelijk op de lijn staan en niet alleen in de collectie.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void CreateHttpClient_ZetAcceptEnAcceptLanguage()
    {
        using var client = SsrfProtection.CreateHttpClient();

        // Op waarde vergelijken, niet op de letterlijke headerstring: HttpClient serialiseert de
        // geparste waarden met spaties na de komma's ("text/html, application/xhtml+xml, ...").
        client.DefaultRequestHeaders.Accept.Should().NotBeEmpty("zonder Accept-header geeft een deel van de clubwebsites 403");
        client.DefaultRequestHeaders.AcceptLanguage.Should().NotBeEmpty("zonder Accept-Language-header geeft een deel van de clubwebsites 403");
        client.DefaultRequestHeaders.Accept.Select(a => a.MediaType)
            .Should().Contain(new[] { "text/html", "application/xhtml+xml", "application/xml", "*/*" });
        client.DefaultRequestHeaders.AcceptLanguage.Select(l => l.Value)
            .Should().Contain(new[] { "nl-NL", "nl", "en" });
        client.DefaultRequestHeaders.UserAgent.Should().NotBeEmpty("de bestaande User-Agent blijft ongewijzigd meegestuurd");
    }

    [Fact]
    public async Task CreateHttpClient_StuurtAcceptHeadersDaadwerkelijkMeeOverDeLijn()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ontvangenVerzoek = ServeAndCaptureRequestAsync(listener, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");

        using var client = SsrfProtection.CreateHttpClient(
            isAllowedEndpointOverride: (_, _) => true, isAllowedPortOverride: _ => true);
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var verzoek = await ontvangenVerzoek;
        listener.Stop();

        verzoek.Should().MatchRegex(@"(?im)^Accept:\s*text/html\b", "de Accept-header moet op de lijn staan, niet alleen in de collectie");
        verzoek.Should().MatchRegex(@"(?im)^Accept-Language:\s*nl-NL\b", "de Accept-Language-header moet op de lijn staan, niet alleen in de collectie");
        verzoek.Should().Contain("User-Agent: SportlinkAdmin/2.0");
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

    /// <summary>
    /// Als <see cref="ServeSingleHttpResponseAsync"/>, maar geeft de rauwe verzoekregels terug —
    /// zodat een test kan controleren wélke headers er daadwerkelijk verstuurd zijn.
    /// </summary>
    private static async Task<string> ServeAndCaptureRequestAsync(TcpListener listener, string rawHttpResponse)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var networkStream = client.GetStream();

        // Doorlezen tot de lege regel die de headers afsluit: één ReadAsync kan de headers over
        // meerdere TCP-segmenten binnenkrijgen, en dan mist de assertie een header die er wél is.
        var verzoek = new System.Text.StringBuilder();
        var buffer = new byte[1024];
        while (!verzoek.ToString().Contains("\r\n\r\n"))
        {
            var gelezen = await networkStream.ReadAsync(buffer);
            if (gelezen == 0) break;
            verzoek.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, gelezen));
        }

        var bytes = System.Text.Encoding.ASCII.GetBytes(rawHttpResponse);
        await networkStream.WriteAsync(bytes);
        await networkStream.FlushAsync();
        return verzoek.ToString();
    }
}
