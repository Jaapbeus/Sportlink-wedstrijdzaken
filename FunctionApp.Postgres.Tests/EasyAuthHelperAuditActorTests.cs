using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using FunctionApp.Postgres.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp.Tests/Admin/EasyAuthHelperAuditActorTests.cs</c>
/// (#1003, zelfde precedent als de SQL Server-tier — geen gedeelde abstractie tussen de tiers, zie
/// ARCHITECTUUR-DATABASE-TIERS.md §2). De audit-actor (<c>public.appsettingsaudit.gewijzigddoor</c>)
/// moet uitsluitend server-side uit gevalideerde Easy Auth-claims komen, nooit uit de request-body
/// of querystring.
/// <para>
/// Manipuleert bewust de procesbrede omgevingsvariabelen <c>WEBSITE_SITE_NAME</c> en
/// <c>LOCAL_AUTH_BYPASS</c> (#1700) — hersteld in een
/// <c>finally</c>-blok. xUnit draait testmethoden binnen één klasse niet parallel aan elkaar, dus dit
/// is veilig zonder extra serialisatie.
/// </para>
/// </summary>
public class EasyAuthHelperAuditActorTests
{
    private const string AzureVar = "WEBSITE_SITE_NAME";
    private const string SchakelaarVar = "LOCAL_AUTH_BYPASS";

    /// <summary>Zet de platformvariabele en de lokale schakelaar (#1700); beide worden hersteld.</summary>
    private static void WithEnv(string? azureHosting, Action assert, string? schakelaar = null)
    {
        var orig = Environment.GetEnvironmentVariable(AzureVar);
        var origSchakelaar = Environment.GetEnvironmentVariable(SchakelaarVar);
        try
        {
            Environment.SetEnvironmentVariable(AzureVar, azureHosting);
            Environment.SetEnvironmentVariable(SchakelaarVar, schakelaar);
            assert();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AzureVar, orig);
            Environment.SetEnvironmentVariable(SchakelaarVar, origSchakelaar);
        }
    }

    private static string EncodePrincipal(params (string typ, string val)[] claims)
    {
        var json = JsonSerializer.Serialize(new
        {
            claims = claims.Select(c => new { typ = c.typ, val = c.val }).ToArray()
        });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static HttpRequest BuildRequest(string? principalHeader = null, string? queryString = null)
    {
        var context = new DefaultHttpContext();
        if (principalHeader != null)
            context.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = principalHeader;
        if (queryString != null)
            context.Request.QueryString = new QueryString(queryString);
        return context.Request;
    }

    [Fact]
    public void GetAuditActor_LokaleOntwikkeling_GeeftHerkenbareSynthetischeIdentiteitOngeachtInput()
    {
        WithEnv(azureHosting: null, schakelaar: "true", assert: () =>
        {
            var req = BuildRequest(queryString: "?gewijzigdDoor=IemandAnders");

            var actor = EasyAuthHelper.GetAuditActor(req);

            actor.Should().Be("lokale-ontwikkelaar");
        });
    }

    [Fact]
    public void GetAuditActor_Productie_NegeertVervalsteQueryparameterEnGebruiktGevalideerdeClaim()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var principal = EncodePrincipal(
                ("roles", "admin"),
                ("preferred_username", "adminA@voorbeeld.nl"),
                ("name", "Admin A"));
            var req = BuildRequest(principal, "?gewijzigdDoor=AdminB");

            var actor = EasyAuthHelper.GetAuditActor(req);

            actor.Should().Be("adminA@voorbeeld.nl",
                "de audit-actor komt uit de gevalideerde claim van de aanroeper, niet uit de querystring");
        });
    }

    [Fact]
    public void GetAuditActor_Productie_GeenEmailClaimMaarWelNaam_ValtTerugOpNaam()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var principal = EncodePrincipal(
                ("roles", "admin"),
                ("name", "Admin Zonder Email"));
            var req = BuildRequest(principal);

            var actor = EasyAuthHelper.GetAuditActor(req);

            actor.Should().Be("Admin Zonder Email");
        });
    }

    [Fact]
    public void GetAuditActor_Productie_GeenEnkeleGevalideerdeClaim_GooitException()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var principal = EncodePrincipal(("roles", "admin"));
            var req = BuildRequest(principal);

            Action act = () => EasyAuthHelper.GetAuditActor(req);

            act.Should().Throw<InvalidOperationException>();
        });
    }

    [Fact]
    public void GetAuditActor_Productie_OntbrekendePrincipalHeader_GooitException()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var req = BuildRequest(principalHeader: null, queryString: "?gewijzigdDoor=onbekend");

            Action act = () => EasyAuthHelper.GetAuditActor(req);

            act.Should().Throw<InvalidOperationException>();
        });
    }

    [Fact]
    public void GetAuditActor_Productie_LegeActorClaim_GooitException()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var principal = EncodePrincipal(("roles", "admin"), ("preferred_username", ""));
            var req = BuildRequest(principal);

            Action act = () => EasyAuthHelper.GetAuditActor(req);

            act.Should().Throw<InvalidOperationException>();
        });
    }

    [Fact]
    public void RequireAdmin_MetAdminRol_StaatToe()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var principal = EncodePrincipal(("roles", "admin"), ("preferred_username", "adminA@voorbeeld.nl"));
            var req = BuildRequest(principal);

            var result = EasyAuthHelper.RequireAdmin(req);

            result.Should().BeNull();
        });
    }

    [Fact]
    public void RequireAdmin_ZonderAdminRol_WordtGeweigerd()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var principal = EncodePrincipal(("roles", "user"), ("preferred_username", "gebruikerB@voorbeeld.nl"));
            var req = BuildRequest(principal);

            var result = EasyAuthHelper.RequireAdmin(req);

            result.Should().NotBeNull();
            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        });
    }

    [Fact]
    public void RequireAdmin_ZonderPrincipal_IsUnauthorized()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var req = BuildRequest();

            var result = EasyAuthHelper.RequireAdmin(req);

            result.Should().BeOfType<UnauthorizedResult>();
        });
    }

    // ── RequireWedstrijdzaken (#1376): admin impliceert Wedstrijdzaken ──

    [Fact]
    public void RequireWedstrijdzaken_MetWedstrijdzakenRol_StaatToe()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var principal = EncodePrincipal(("roles", "Wedstrijdzaken"));
            var req = BuildRequest(principal);

            var result = EasyAuthHelper.RequireWedstrijdzaken(req);

            result.Should().BeNull();
        });
    }

    [Fact]
    public void RequireWedstrijdzaken_MetAdminRolMaarZonderWedstrijdzaken_StaatToe()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var principal = EncodePrincipal(("roles", "admin"), ("preferred_username", "adminA@voorbeeld.nl"));
            var req = BuildRequest(principal);

            var result = EasyAuthHelper.RequireWedstrijdzaken(req);

            result.Should().BeNull("admin impliceert sinds #1376 de rol Wedstrijdzaken");
        });
    }

    [Fact]
    public void RequireWedstrijdzaken_MetUserRolZonderAdminOfWedstrijdzaken_WordtGeweigerd()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var principal = EncodePrincipal(("roles", "user"), ("preferred_username", "gebruikerB@voorbeeld.nl"));
            var req = BuildRequest(principal);

            var result = EasyAuthHelper.RequireWedstrijdzaken(req);

            result.Should().NotBeNull();
            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        });
    }

    // ── IsInRole/IsAdmin (#1341): niet-gooiende variant van RequireRole ──

    [Fact]
    public void IsAdmin_LokaleOntwikkeling_IsAltijdWaar()
    {
        WithEnv(azureHosting: null, schakelaar: "true", assert: () =>
        {
            var req = BuildRequest();

            EasyAuthHelper.IsAdmin(req).Should().BeTrue();
        });
    }

    [Fact]
    public void IsAdmin_ProductieMetAdminRol_IsWaar()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var principal = EncodePrincipal(("roles", "admin"));
            var req = BuildRequest(principal);

            EasyAuthHelper.IsAdmin(req).Should().BeTrue();
        });
    }

    [Fact]
    public void IsAdmin_ProductieZonderAdminRol_IsOnwaar()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var principal = EncodePrincipal(("roles", "Wedstrijdzaken"));
            var req = BuildRequest(principal);

            EasyAuthHelper.IsAdmin(req).Should().BeFalse();
        });
    }

    [Fact]
    public void IsAdmin_ProductieZonderPrincipal_IsOnwaar()
    {
        WithEnv(azureHosting: "func-test-01", () =>
        {
            var req = BuildRequest();

            EasyAuthHelper.IsAdmin(req).Should().BeFalse();
        });
    }

    // ── Lokale bypass alleen met de schakelaar en nooit in Azure (#1700) ──

    public static IEnumerable<object?[]> AfgedwongenOmgevingen() =>
    [
        [null, null],           // buiten Azure zonder schakelaar (CI, losse func start, andere hosting)
        ["func-test-01", "true"], // Azure met een geweigerde schakelaar
        ["func-test-01", null],   // productie
    ];

    [Theory]
    [MemberData(nameof(AfgedwongenOmgevingen))]
    public void RequireAdmin_ZonderBypass_ZonderPrincipal_IsUnauthorized(string? azure, string? schakelaar)
        => WithEnv(azure, schakelaar: schakelaar, assert: () =>
            EasyAuthHelper.RequireAdmin(BuildRequest()).Should().BeOfType<UnauthorizedResult>());

    [Theory]
    [MemberData(nameof(AfgedwongenOmgevingen))]
    public void RequireAdmin_ZonderBypass_AlleenUserRol_Geeft403(string? azure, string? schakelaar)
        => WithEnv(azure, schakelaar: schakelaar, assert: () =>
            EasyAuthHelper.RequireAdmin(BuildRequest(EncodePrincipal(("roles", "user"))))
                .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403));

    [Theory]
    [MemberData(nameof(AfgedwongenOmgevingen))]
    public void IsAdmin_ZonderBypass_ZonderPrincipal_IsOnwaar(string? azure, string? schakelaar)
        => WithEnv(azure, schakelaar: schakelaar, assert: () =>
            EasyAuthHelper.IsAdmin(BuildRequest()).Should().BeFalse());

    [Theory]
    [MemberData(nameof(AfgedwongenOmgevingen))]
    public void GetAuditActor_ZonderBypass_ZonderPrincipal_GooitException(string? azure, string? schakelaar)
        => WithEnv(azure, schakelaar: schakelaar, assert: () =>
        {
            Action act = () => EasyAuthHelper.GetAuditActor(BuildRequest());
            act.Should().Throw<InvalidOperationException>("zonder bypass is er nooit een synthetische identiteit");
        });

    [Theory]
    [MemberData(nameof(AfgedwongenOmgevingen))]
    public void GetLerenAanroeper_ZonderBypass_VerwachtEenPrincipal(string? azure, string? schakelaar)
        => WithEnv(azure, schakelaar: schakelaar, assert: () =>
            EasyAuthHelper.GetLerenAanroeper(BuildRequest()).DoorId.Should().Be("onbekend",
                "zonder bypass wordt een aanroeper zonder principal nooit als lokale ontwikkelaar vastgelegd"));

    [Fact]
    public void GetLerenAanroeper_LokaalMetSchakelaar_IsDeLokaleOntwikkelaar()
        => WithEnv(azureHosting: null, schakelaar: "true", assert: () =>
            EasyAuthHelper.GetLerenAanroeper(BuildRequest()).DoorId.Should().Be("lokale-ontwikkelaar"));

    [Theory]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("")]
    public void RequireAdmin_LokaalMetAndereSchakelaarwaarde_BlijftDicht(string waarde)
        => WithEnv(azureHosting: null, schakelaar: waarde, assert: () =>
            EasyAuthHelper.RequireAdmin(BuildRequest()).Should().BeOfType<UnauthorizedResult>());
}
