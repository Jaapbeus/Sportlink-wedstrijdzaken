using Planner.Endpoints.Email;
using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Endpoints.Sportlink;

/// <summary>
/// Beslissing of de contract-check-noodmail verstuurd mag worden, gedeeld door beide databasetiers
/// (eigenaarsbesluit #1815: hoogstens één mail per Nederlandse kalenderdag).
/// </summary>
public static class ContractCheckNoodmailCore
{
    /// <summary>
    /// <c>true</c> alleen bij een afwijking (<paramref name="isOk"/> = <c>false</c>) waarvoor vandaag
    /// (Europe/Amsterdam) nog geen mail is verstuurd. Een geslaagde check raakt de registratie van het
    /// laatste verzendmoment bewust niet aan: de kalenderdag alleen beslist, zodat de reeks
    /// afwijking, geslaagd, afwijking op één dag niet tot een tweede mail leidt.
    /// </summary>
    public static async Task<bool> MagNoodmailVersturenAsync(
        bool isOk, INoodmailThrottlePoort throttleStore, string sleutel, DateTime nuUtc)
    {
        if (isOk) return false;
        var laatsteKeer = await throttleStore.LaatsteKeerVerstuurdAsync(sleutel);
        return SportlinkEndpointCore.MagContractCheckNoodmailVersturen(laatsteKeer, nuUtc);
    }
}
