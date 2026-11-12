using AwesomeAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Endpoints.Sportlink;
using SportlinkFunction.Email;
using SportlinkFunction.Sportlink;
using Xunit;

namespace FunctionApp.Tests.Sportlink;

/// <summary>
/// #1815 per tier: met de echte verzendregistratie van de timer (<c>StuurContractCheckNoodmailAsync</c>) en de
/// gedeelde beslissing geeft afwijking, geslaagd, afwijking op één Nederlandse kalenderdag één mail.
/// </summary>
public class ContractCheckNoodmailSequentieTests
{
    [Fact]
    public async Task AfwijkingGeslaagdAfwijking_ZelfdeDag_EenMail_VolgendeDagWeer()
    {
        var store = new FakeNoodmailThrottleStore();
        var graph = new Mock<IEmailGraphService>();
        var sleutel = SportlinkContractCheckTimerFunction.NoodmailSleutel;

        // De timer registreert op "nu"; de beslissing gebruikt dezelfde klok, dus alles op vandaag.
        var nu = DateTime.UtcNow;
        if (await ContractCheckNoodmailCore.MagNoodmailVersturenAsync(false, store, sleutel, nu))
            await SportlinkContractCheckTimerFunction.StuurContractCheckNoodmailAsync(graph.Object, "afwijking", store, NullLogger.Instance);

        (await ContractCheckNoodmailCore.MagNoodmailVersturenAsync(true, store, sleutel, nu)).Should().BeFalse();

        if (await ContractCheckNoodmailCore.MagNoodmailVersturenAsync(false, store, sleutel, nu))
            await SportlinkContractCheckTimerFunction.StuurContractCheckNoodmailAsync(graph.Object, "afwijking", store, NullLogger.Instance);

        graph.Verify(g => g.SendReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), null,
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<global::Planner.Shared.Email.EmailBijlage?>()), Times.Once);

        (await ContractCheckNoodmailCore.MagNoodmailVersturenAsync(false, store, sleutel, nu.AddDays(2))).Should().BeTrue();
    }
}
