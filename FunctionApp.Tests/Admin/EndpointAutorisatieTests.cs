using System.Reflection;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

/// <summary>
/// Autorisatie-regressietest per HTTP-endpoint (#1350) — de SQL Server-tier. Tegenhanger van
/// <c>FunctionApp.Postgres.Tests/EndpointAutorisatieTests.cs</c>; zie de klasse-doc daar voor de
/// volledige toelichting. Kort: voor élk HTTP-endpoint in de assembly bewijst deze test dat
/// zonder principal <c>401</c> volgt, met alleen de rol <c>user</c> <c>403</c>, en dat de vereiste
/// rol(len) de poort van <see cref="AdminEndpoint"/> passeren — via de testhaak
/// <see cref="AdminEndpoint.PoortGepasseerdVoorTests"/>, zodat geen database wordt geraakt en geen
/// endpoint echt werk doet.
/// <para>
/// Bewust een eigen kopie per tier en geen gedeeld testbestand: de test grijpt aan op de
/// tier-eigen <c>AdminEndpoint</c>/<c>EasyAuthHelper</c>, en ARCHITECTUUR-DATABASE-TIERS.md §2
/// verbiedt een cross-tree-koppeling tussen de twee testassemblies.
/// </para>
/// </summary>
public class EndpointAutorisatieTests
{
    private const string AzureVar = "WEBSITE_SITE_NAME";

    private static readonly string[] AnoniemeRoutes = ["health"];

    private static readonly Dictionary<string, int> DirectePoortMetVerwachteStatus = new()
    {
        ["AdminThemeExtract"] = 400, // lege url → ThemeCore weigert vóór elke databaseaanroep
        ["AdminGeocodeGet"] = 400,   // lege plaatsnaam → eigen validatie, geen database
    };

    private const int MinimaalVerwachtAantalEndpoints = 90;

    // ── Ontdekking ─────────────────────────────────────────────────────────────────────────────

    public sealed record HttpEndpoint(string Naam, string Route, AuthorizationLevel Niveau, MethodInfo Methode)
    {
        public bool IsSportlink => Route.StartsWith("sportlink/", StringComparison.Ordinal);
        public bool IsAnoniem => AnoniemeRoutes.Contains(Route, StringComparer.Ordinal);
        public override string ToString() => $"{Naam} [{Route}]";
    }

    internal static IReadOnlyList<HttpEndpoint> VindAlleHttpEndpoints()
    {
        var lijst = new List<HttpEndpoint>();
        foreach (var type in typeof(AdminEndpoint).Assembly.GetTypes())
        {
            foreach (var methode in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var functie = methode.GetCustomAttribute<FunctionAttribute>();
                if (functie == null) continue;
                var trigger = methode.GetParameters()
                    .Select(p => p.GetCustomAttribute<HttpTriggerAttribute>())
                    .FirstOrDefault(t => t != null);
                if (trigger == null) continue; // timer/queue: geen aanroeper met een rol
                lijst.Add(new HttpEndpoint(functie.Name, trigger.Route ?? functie.Name, trigger.AuthLevel, methode));
            }
        }
        return lijst.OrderBy(e => e.Naam, StringComparer.Ordinal).ToList();
    }

    public static IEnumerable<object[]> BeveiligdeEndpoints() =>
        VindAlleHttpEndpoints().Where(e => !e.IsAnoniem).Select(e => new object[] { e.Naam });

    public static IEnumerable<object[]> SportlinkEndpoints() =>
        VindAlleHttpEndpoints().Where(e => e.IsSportlink).Select(e => new object[] { e.Naam });

    private static HttpEndpoint Endpoint(string naam) =>
        VindAlleHttpEndpoints().Single(e => e.Naam == naam);

    // ── Structurele controles ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Reflectie_VindtAlleHttpEndpoints()
    {
        var endpoints = VindAlleHttpEndpoints();

        endpoints.Count.Should().BeGreaterThanOrEqualTo(MinimaalVerwachtAantalEndpoints,
            "een lagere telling betekent dat de ontdekking stuk is, niet dat er minder endpoints zijn");
        endpoints.Select(e => e.Naam).Should().OnlyHaveUniqueItems();
        endpoints.Should().Contain(e => e.Route == "sync-matches",
            "de handmatige sync hoort sinds #1350 een gewoon beveiligd endpoint te zijn");
    }

    [Fact]
    public void GeenEnkelEndpoint_AccepteertNogEenFunctionKey()
    {
        VindAlleHttpEndpoints()
            .Where(e => e.Niveau != AuthorizationLevel.Anonymous)
            .Should().BeEmpty("elk endpoint hoort op AuthorizationLevel.Anonymous + EasyAuthHelper te staan (#1350)");
    }

    [Fact]
    public void AnoniemeAllowlist_BestaatUitsluitendUitBestaandeRoutes()
    {
        var routes = VindAlleHttpEndpoints().Select(e => e.Route).ToHashSet(StringComparer.Ordinal);
        AnoniemeRoutes.Should().OnlyContain(r => routes.Contains(r), "een verdwenen route hoort uit de allowlist");
        DirectePoortMetVerwachteStatus.Keys.Should().OnlyContain(n => VindAlleHttpEndpoints().Any(e => e.Naam == n));
    }

    // ── Per endpoint: dicht zonder rol ─────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BeveiligdeEndpoints))]
    public async Task ZonderPrincipal_Geeft401(string naam)
    {
        var endpoint = Endpoint(naam);
        await MetProductieOmgeving(async () =>
        {
            var haakAangeroepen = false;
            AdminEndpoint.PoortGepasseerdVoorTests = _ => { haakAangeroepen = true; return new PoortGepasseerdResult(); };

            var result = await Roep(endpoint, principal: null);

            result.Should().BeOfType<UnauthorizedResult>($"{endpoint} moet zonder principal 401 geven");
            haakAangeroepen.Should().BeFalse("de poort mag zonder principal nooit gepasseerd worden");
        });
    }

    [Theory]
    [MemberData(nameof(BeveiligdeEndpoints))]
    public async Task MetAlleenUserRol_Geeft403(string naam)
    {
        var endpoint = Endpoint(naam);
        await MetProductieOmgeving(async () =>
        {
            var haakAangeroepen = false;
            AdminEndpoint.PoortGepasseerdVoorTests = _ => { haakAangeroepen = true; return new PoortGepasseerdResult(); };

            var result = await Roep(endpoint, Principal(("roles", "user"), ("preferred_username", "gebruiker@voorbeeld.nl")));

            StatusVan(result).Should().Be(403, $"{endpoint} moet de rol 'user' weigeren — RequireAuthenticated bestaat niet meer (#1350)");
            haakAangeroepen.Should().BeFalse();
        });
    }

    // ── Per endpoint: open mét de vereiste rol, en wel via de wrapper ──────────────────────────

    [Theory]
    [MemberData(nameof(BeveiligdeEndpoints))]
    public async Task MetVereisteRollen_PasseertDePoortViaDeWrapper(string naam)
    {
        var endpoint = Endpoint(naam);
        await MetProductieOmgeving(async () =>
        {
            AdminEndpoint.PoortGepasseerdVoorTests = _ => new PoortGepasseerdResult();

            var rollen = endpoint.IsSportlink
                ? Principal(("roles", "admin"), ("roles", "Wedstrijdzaken"), ("preferred_username", "admin@voorbeeld.nl"))
                : Principal(("roles", "admin"), ("preferred_username", "admin@voorbeeld.nl"));

            var result = await Roep(endpoint, rollen);

            if (DirectePoortMetVerwachteStatus.TryGetValue(endpoint.Naam, out var verwachteStatus))
            {
                StatusVan(result).Should().Be(verwachteStatus,
                    $"{endpoint} roept de poort bewust direct aan en moet mét admin zijn eigen invoervalidatie bereiken");
            }
            else
            {
                result.Should().BeOfType<PoortGepasseerdResult>(
                    $"{endpoint} moet mét de vereiste rol de poort van AdminEndpoint passeren — een ander resultaat betekent " +
                    "óf een strengere poort dan bedoeld, óf een endpoint dat niet via de wrapper loopt");
            }
        });
    }

    // ── Sportlink: beide poorten, in deze volgorde ─────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(SportlinkEndpoints))]
    public async Task Sportlink_AlleenAdmin_WordtGeweigerdOpDeWedstrijdzakenPoort(string naam)
    {
        var endpoint = Endpoint(naam);
        await MetProductieOmgeving(async () =>
        {
            AdminEndpoint.PoortGepasseerdVoorTests = _ => new PoortGepasseerdResult();

            var result = await Roep(endpoint, Principal(("roles", "admin")));

            StatusVan(result).Should().Be(403, $"{endpoint} vereist de rol Wedstrijdzaken bovenop admin (#1272)");
        });
    }

    [Theory]
    [MemberData(nameof(SportlinkEndpoints))]
    public async Task Sportlink_AlleenWedstrijdzaken_WordtGeweigerdOpDeAdminPoort(string naam)
    {
        var endpoint = Endpoint(naam);
        await MetProductieOmgeving(async () =>
        {
            AdminEndpoint.PoortGepasseerdVoorTests = _ => new PoortGepasseerdResult();

            var result = await Roep(endpoint, Principal(("roles", "Wedstrijdzaken")));

            StatusVan(result).Should().Be(403, $"{endpoint} vereist de rol admin bovenop Wedstrijdzaken (#1272)");
        });
    }

    // ── De wrapper zelf ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminEndpoint_ZonderPrincipal_RoeptWerkNietAan()
    {
        await MetProductieOmgeving(async () =>
        {
            var werkAangeroepen = false;
            var result = await AdminEndpoint.ExecuteAsync(Verzoek(null), NullLogger.Instance, "test",
                _ => { werkAangeroepen = true; return Task.FromResult<IActionResult>(new OkResult()); });

            result.Should().BeOfType<UnauthorizedResult>();
            werkAangeroepen.Should().BeFalse();
        });
    }

    [Fact]
    public async Task AdminEndpoint_ZonderDatabase_ZonderPrincipal_RoeptWerkNietAan()
    {
        await MetProductieOmgeving(async () =>
        {
            var werkAangeroepen = false;
            var result = await AdminEndpoint.ExecuteZonderDatabaseAsync(Verzoek(null), NullLogger.Instance, "test",
                () => { werkAangeroepen = true; return Task.FromResult<IActionResult>(new OkResult()); });

            result.Should().BeOfType<UnauthorizedResult>();
            werkAangeroepen.Should().BeFalse();
        });
    }

    [Fact]
    public async Task AdminEndpoint_ZonderDatabase_MetAdmin_RoeptWerkAanZonderDatabase()
    {
        await MetProductieOmgeving(async () =>
        {
            var werkAangeroepen = false;
            var result = await AdminEndpoint.ExecuteZonderDatabaseAsync(
                Verzoek(Principal(("roles", "admin"))), NullLogger.Instance, "test",
                () => { werkAangeroepen = true; return Task.FromResult<IActionResult>(new OkResult()); });

            result.Should().BeOfType<OkResult>();
            werkAangeroepen.Should().BeTrue();
        });
    }

    [Fact]
    public async Task AdminEndpoint_ZonderDatabase_UitzonderingInWerk_Geeft500ZonderDetails()
    {
        await MetProductieOmgeving(async () =>
        {
            var result = await AdminEndpoint.ExecuteZonderDatabaseAsync(
                Verzoek(Principal(("roles", "admin"))), NullLogger.Instance, "test",
                () => throw new InvalidOperationException("geheim detail"));

            var obj = result.Should().BeOfType<ObjectResult>().Which;
            obj.StatusCode.Should().Be(500);
            obj.Value!.ToString().Should().NotContain("geheim detail");
        });
    }

    // ── Hulpmiddelen ───────────────────────────────────────────────────────────────────────────

    public sealed class PoortGepasseerdResult : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context) => Task.CompletedTask;
    }

    private static async Task MetProductieOmgeving(Func<Task> test)
    {
        var origSite = Environment.GetEnvironmentVariable(AzureVar);
        try
        {
            Environment.SetEnvironmentVariable(AzureVar, "func-test-01");
            await test();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AzureVar, origSite);
            AdminEndpoint.PoortGepasseerdVoorTests = null;
        }
    }

    private static string Principal(params (string typ, string val)[] claims)
    {
        var json = JsonSerializer.Serialize(new
        {
            claims = claims.Select(c => new { typ = c.typ, val = c.val }).ToArray()
        });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static HttpRequest Verzoek(string? principal)
    {
        var context = new DefaultHttpContext();
        if (principal != null)
            context.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = principal;
        context.Request.Body = new MemoryStream();
        return context.Request;
    }

    private static FunctionContext FunctieContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var context = new Mock<FunctionContext>();
        context.Setup(c => c.InstanceServices).Returns(provider);
        return context.Object;
    }

    private static async Task<IActionResult> Roep(HttpEndpoint endpoint, string? principal)
    {
        if (!endpoint.Methode.IsStatic)
            throw new InvalidOperationException(
                $"{endpoint} is een instantiemethode; breid deze test uit met DI-constructie voordat je zo'n endpoint toevoegt.");

        var req = Verzoek(principal);
        var args = endpoint.Methode.GetParameters().Select(p => p.ParameterType switch
        {
            var t when t == typeof(HttpRequest) => (object)req,
            var t when t == typeof(FunctionContext) => FunctieContext(),
            var t when t == typeof(string) => "proef",
            var t when t == typeof(int) => 1,
            var t when t == typeof(long) => 1L,
            var t when t == typeof(Guid) => Guid.NewGuid(),
            var t when t == typeof(bool) => false,
            var t => throw new InvalidOperationException($"{endpoint}: onbekend parametertype {t.Name} ({p.Name}) — breid de test uit.")
        }).ToArray();

        var uitkomst = endpoint.Methode.Invoke(null, args)
            ?? throw new InvalidOperationException($"{endpoint} gaf null terug");
        if (uitkomst is Task<IActionResult> taak) return await taak;
        if (uitkomst is IActionResult direct) return direct;
        throw new InvalidOperationException($"{endpoint}: onverwacht retourtype {uitkomst.GetType().Name}");
    }

    private static int? StatusVan(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode,
        StatusCodeResult s => s.StatusCode,
        _ => null
    };
}
