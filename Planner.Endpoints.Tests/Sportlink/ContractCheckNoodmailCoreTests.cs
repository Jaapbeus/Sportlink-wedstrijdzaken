using AwesomeAssertions;
using Planner.Endpoints.Sportlink;
using Planner.Endpoints.Tests.Email;
using Xunit;

namespace Planner.Endpoints.Tests.Sportlink;

/// <summary>
/// Eigenaarsbesluit #1815: hoogstens één contract-check-noodmail per Nederlandse kalenderdag. Een geslaagde
/// check mag het verzendmoment niet wissen (reeks afwijking, geslaagd, afwijking op één dag = één mail).
/// </summary>
public class ContractCheckNoodmailCoreTests
{
    private const string Sleutel = "contract-check-test";

    [Fact]
    public async Task AfwijkingGeslaagdAfwijking_OpEenKalenderdag_GeeftEenMail()
    {
        var store = new FakeThrottle();
        var mails = 0;

        // Ochtend (UTC+1): afwijking, eerste mail.
        var t1 = new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc);
        if (await ContractCheckNoodmailCore.MagNoodmailVersturenAsync(false, store, Sleutel, t1))
        {
            mails++;
            await store.RegistreerVerstuurdAsync(Sleutel, t1);
        }

        // Herstel op dezelfde dag: mag de registratie niet wissen.
        (await ContractCheckNoodmailCore.MagNoodmailVersturenAsync(true, store, Sleutel, t1.AddHours(2))).Should().BeFalse();
        (await store.LaatsteKeerVerstuurdAsync(Sleutel)).Should().Be(t1);

        // Opnieuw afwijking dezelfde Nederlandse dag: geen tweede mail.
        var t3 = t1.AddHours(4);
        if (await ContractCheckNoodmailCore.MagNoodmailVersturenAsync(false, store, Sleutel, t3))
            mails++;

        mails.Should().Be(1);
    }

    [Fact]
    public async Task AfwijkingGeslaagdAfwijking_VolgendeKalenderdag_MagOpnieuw()
    {
        var store = new FakeThrottle();
        var t1 = new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc);
        await store.RegistreerVerstuurdAsync(Sleutel, t1);
        await ContractCheckNoodmailCore.MagNoodmailVersturenAsync(true, store, Sleutel, t1.AddHours(2));

        // 15 jan 23:30 UTC is al 16 jan 00:30 in Nederland.
        var volgendeDag = new DateTime(2026, 1, 15, 23, 30, 0, DateTimeKind.Utc);
        (await ContractCheckNoodmailCore.MagNoodmailVersturenAsync(false, store, Sleutel, volgendeDag)).Should().BeTrue();
    }

    [Fact]
    public async Task NooitVerstuurd_BijAfwijking_MagWel_EnGeslaagdNooit()
    {
        var store = new FakeThrottle();
        var nu = new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc);
        (await ContractCheckNoodmailCore.MagNoodmailVersturenAsync(false, store, Sleutel, nu)).Should().BeTrue();
        (await ContractCheckNoodmailCore.MagNoodmailVersturenAsync(true, store, Sleutel, nu)).Should().BeFalse();
    }
}
