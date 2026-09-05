using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

/// <summary>
/// Regressietests voor #1003: de audit-actor (<c>dbo.AppSettingsAudit.GewijzigdDoor</c>) moet
/// uitsluitend server-side uit gevalideerde Easy Auth-claims komen, nooit uit de request-body of
/// querystring. Vóór deze fix kon een geldige admin-token <c>GewijzigdDoor</c> vrij kiezen via
/// <c>updateRequest.GewijzigdDoor ?? req.Query["gewijzigdDoor"]</c>.
/// <para>
/// Manipuleert bewust de procesbrede omgevingsvariabele <c>WEBSITE_SITE_NAME</c> — hersteld in een
/// <c>finally</c>-blok via <see cref="WithEnv"/>. Zelfde precedent als
/// <c>FunctionApp.Tests/Infrastructure/EgressGuardTests.cs</c>: xUnit draait testmethoden binnen één
/// klasse niet parallel aan elkaar, dus dit is veilig zonder extra serialisatie.
/// </para>
/// </summary>
public class EasyAuthHelperAuditActorTests
{
    private const string AzureVar = "WEBSITE_SITE_NAME";

    private static void WithEnv(string? azureHosting, Action assert)
    {
        var orig = Environment.GetEnvironmentVariable(AzureVar);
        try
        {
            Environment.SetEnvironmentVariable(AzureVar, azureHosting);
            assert();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AzureVar, orig);
        }
    }

    /// <summary>Bouwt een X-MS-CLIENT-PRINCIPAL-header-waarde zoals Easy Auth die injecteert.</summary>
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
        WithEnv(azureHosting: null, () =>
        {
            // Vervalste queryparameter aanwezig — mag geen enkele invloed hebben.
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
            // Admin A probeert de audit-actor te vervalsen naar "AdminB" via de querystring —
            // exact het aanvalsscenario uit #1003.
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
            // Principal aanwezig (roles-claim voor RequireAdmin), maar geen upn/preferred_username/
            // email/name — mag NOOIT stilzwijgend als "onbekend" worden vastgelegd.
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
}
