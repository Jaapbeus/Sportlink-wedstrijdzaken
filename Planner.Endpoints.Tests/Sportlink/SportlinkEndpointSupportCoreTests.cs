using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Endpoints.Tests.Sportlink;

/// <summary>
/// Tests voor de endpoint-orkestratie die tot #1271 op beide tiers woordelijk gekopieerd stond
/// (#1122, #1266) — routeparameter/DI-plumbing en de vertaling naar <see cref="IActionResult"/>.
/// De beslislogica zelf (<see cref="SportlinkEndpointCore"/>) is al gedekt in
/// Planner.Shared.Tests; deze suite dekt alleen wat hier nieuw is: de orkestratie ZELF, en de
/// vertaling van een gedeelde fout naar een concrete HTTP-respons.
/// </summary>
public class SportlinkEndpointSupportCoreTests
{
    private static FunctionContext MetClient(ISportlinkClubClient? client)
    {
        var services = new ServiceCollection();
        if (client != null) services.AddSingleton(client);
        var provider = services.BuildServiceProvider();

        var context = new Mock<FunctionContext>();
        context.Setup(c => c.InstanceServices).Returns(provider);
        return context.Object;
    }

    // ── NaarActionResult / ClientNietGeconfigureerdFout ────────────────────────────────────────

    [Fact]
    public void NaarActionResult_ZetHttpStatusEnFoutmeldingOver()
    {
        var result = SportlinkEndpointSupportCore.NaarActionResult(new SportlinkEndpointFout(503, "even geduld"))
            as ObjectResult;

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(503);
    }

    [Fact]
    public void ClientNietGeconfigureerdFout_Geeft503()
    {
        var result = SportlinkEndpointSupportCore.ClientNietGeconfigureerdFout() as ObjectResult;

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(SportlinkEndpointCore.ClientNietGeconfigureerdFout.HttpStatus);
    }

    // ── ControleerToggleEnEgress / IsDryRunActief: bewijst de delegate-doorgifte ───────────────
    // SportlinkEndpointCore zelf is al uitputtend getest in Planner.Shared.Tests — deze twee tests
    // bewijzen alleen dat de delegates hier goed worden doorgegeven en vertaald naar HTTP.

    [Fact]
    public void ControleerToggleEnEgress_ExtensieUit_Geeft409()
    {
        var fout = SportlinkEndpointSupportCore.ControleerToggleEnEgress(
            leesInstelling: _ => "0", egressToegestaan: () => true) as ObjectResult;

        fout.Should().NotBeNull();
        fout!.StatusCode.Should().Be(409);
    }

    [Fact]
    public void ControleerToggleEnEgress_AllesInOrde_GeeftNull()
        => SportlinkEndpointSupportCore.ControleerToggleEnEgress(
            leesInstelling: _ => "1", egressToegestaan: () => true).Should().BeNull();

    [Fact]
    public void IsDryRunActief_GeeftDelegateResultaatDoor()
        => SportlinkEndpointSupportCore.IsDryRunActief(_ => "0").Should().BeFalse();

    // ── ExecuteWedstrijdzakenAsync: de kern van #1272 — twee poorten na elkaar ─────────────────

    [Fact]
    public async Task ExecuteWedstrijdzakenAsync_RolFout_RoeptAdminExecuteNietAan()
    {
        var rolFout = new ObjectResult("geen Wedstrijdzaken-rol") { StatusCode = 403 };
        var adminAangeroepen = false;

        var result = await SportlinkEndpointSupportCore.ExecuteWedstrijdzakenAsync(
            req: null!,
            log: NullLogger.Instance,
            errorContext: "test",
            work: _ => Task.FromResult<IActionResult>(new OkResult()),
            requireWedstrijdzaken: _ => rolFout,
            adminExecuteAsync: (_, _, _, _) =>
            {
                adminAangeroepen = true;
                return Task.FromResult<IActionResult>(new OkResult());
            });

        result.Should().BeSameAs(rolFout, "een ontbrekende Wedstrijdzaken-rol moet de admin-poort nooit bereiken");
        adminAangeroepen.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteWedstrijdzakenAsync_RolOk_RoeptAdminExecuteAan()
    {
        var adminResultaat = new OkResult();

        var result = await SportlinkEndpointSupportCore.ExecuteWedstrijdzakenAsync(
            req: null!,
            log: NullLogger.Instance,
            errorContext: "test",
            work: _ => Task.FromResult<IActionResult>(new OkResult()),
            requireWedstrijdzaken: _ => null,
            adminExecuteAsync: (_, _, _, _) => Task.FromResult<IActionResult>(adminResultaat));

        result.Should().BeSameAs(adminResultaat);
    }

    // ── ClientOfFout / ClientVoorTimer: DI-lookup via FunctionContext ──────────────────────────

    [Fact]
    public void ClientOfFout_ClientGeregistreerd_GeeftClientTerug()
    {
        var client = Mock.Of<ISportlinkClubClient>();

        var (gevondenClient, fout) = SportlinkEndpointSupportCore.ClientOfFout(MetClient(client));

        gevondenClient.Should().BeSameAs(client);
        fout.Should().BeNull();
    }

    [Fact]
    public void ClientOfFout_ClientNietGeregistreerd_Geeft503()
    {
        var (client, fout) = SportlinkEndpointSupportCore.ClientOfFout(MetClient(null));

        client.Should().BeNull();
        (fout as ObjectResult)?.StatusCode.Should().Be(SportlinkEndpointCore.ClientNietGeconfigureerdFout.HttpStatus);
    }

    [Fact]
    public void ClientVoorTimer_ExtensieUit_GeeftNullZonderDiTeRaadplegen()
    {
        var context = MetClient(Mock.Of<ISportlinkClubClient>());

        var client = SportlinkEndpointSupportCore.ClientVoorTimer(
            context, NullLogger.Instance, "test-taak",
            leesInstelling: _ => "0", egressToegestaan: () => true);

        client.Should().BeNull("de toggle staat uit, dus de timer moet stoppen vóór de DI-lookup");
    }

    [Fact]
    public void ClientVoorTimer_AllesInOrde_GeeftClientTerug()
    {
        var verwachteClient = Mock.Of<ISportlinkClubClient>();
        var context = MetClient(verwachteClient);

        var client = SportlinkEndpointSupportCore.ClientVoorTimer(
            context, NullLogger.Instance, "test-taak",
            leesInstelling: _ => "1", egressToegestaan: () => true);

        client.Should().BeSameAs(verwachteClient);
    }

    // ── RondMutatieAfAsync: de audit-afronding als delegate (bewust geen ISportlinkMutationAuditService
    //    hier — zie de klassecomment van SportlinkEndpointSupportCore) ─────────────────────────

    [Fact]
    public async Task RondMutatieAfAsync_Succes_RoeptAuditDelegateMetJuisteWaardenAan()
    {
        string? ontvangenResultaat = null;
        string? ontvangenSamenvatting = "niet-null-sentinel";

        var respons = new SportlinkClubResponse<string>(SportlinkClubCallStatus.Ok, "ok-data", null, 200);

        var result = await SportlinkEndpointSupportCore.RondMutatieAfAsync(
            respons,
            voltooiAuditAsync: (resultaat, samenvatting) =>
            {
                ontvangenResultaat = resultaat;
                ontvangenSamenvatting = samenvatting;
                return Task.CompletedTask;
            },
            naarMutatieResultaat: data => new SportlinkMutationResult(true, null, false, false),
            ok: data => new OkObjectResult(data));

        ontvangenResultaat.Should().Be("Success");
        ontvangenSamenvatting.Should().BeNull();
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task RondMutatieAfAsync_GeenAuditDelegate_SlaagtZonderAudit()
    {
        var respons = new SportlinkClubResponse<string>(SportlinkClubCallStatus.Ok, "ok-data", null, 200);

        var result = await SportlinkEndpointSupportCore.RondMutatieAfAsync(
            respons,
            voltooiAuditAsync: null,
            naarMutatieResultaat: data => new SportlinkMutationResult(true, null, false, false),
            ok: data => new OkObjectResult(data));

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task RondMutatieAfAsync_TransportFout_GeeftVertaaldeFoutEnRoeptOkNietAan()
    {
        var respons = new SportlinkClubResponse<string>(SportlinkClubCallStatus.NetwerkFout, null, "verbinding verbroken", null);
        var okAangeroepen = false;

        var result = await SportlinkEndpointSupportCore.RondMutatieAfAsync(
            respons,
            voltooiAuditAsync: (_, _) => Task.CompletedTask,
            naarMutatieResultaat: data => new SportlinkMutationResult(true, null, false, false),
            ok: data =>
            {
                okAangeroepen = true;
                return new OkObjectResult(data);
            });

        okAangeroepen.Should().BeFalse();
        (result as ObjectResult)?.StatusCode.Should().Be(502);
    }
}
