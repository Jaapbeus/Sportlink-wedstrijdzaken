using FunctionApp.Postgres.Infrastructure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Planner.Shared.Planning;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Past de Sportlink-veldplanner toe op de Planning (#1563, #1582): Sportlink is leidend. Veld, starttijd en blokduur
/// komen uit Sportlink; een Sportlink-blok zonder eigen regel wordt zelf een regel en een eigen regel die Sportlink
/// niet kent krijgt de markering "niet in Sportlink". Is Sportlink niet bereikbaar, dan blijft de eigen berekening.
/// De regels staan in <see cref="SportlinkVeldbezettingSamenvoeging"/> en <see cref="VeldplannerOverlayCore"/>;
/// hier alleen de tier-eigen toepassing op <see cref="VeldbezettingItem"/>.
/// </summary>
internal static class VeldbezettingSportlinkOverlay
{
    internal static async Task<List<VeldbezettingItem>> PasToeAsync(
        List<VeldbezettingItem> items, FunctionContext context, DateOnly datum, string clubCode, ILogger log)
        => (await VeldplannerOverlayCore.SamenvoegAsync<VeldbezettingItem>(
                items, clubCode, context, PostgresAppSettings.GetSetting, EgressGuard.ExternalIntegrationsAllowed, datum, log,
                sleutel: i => (i.Wedstrijd, i.AanvangsTijd),
                overschrijf: (w, b) => w with
                {
                    AanvangsTijd = b.StartTijd, Veld = b.Veld, DuurMinuten = b.DuurMinuten, Veldafmeting = b.Veldafmeting, Bron = SportlinkVeldbezettingSamenvoeging.BronSportlink
                },
                nieuw: NieuweRegel,
                nietInSportlink: w => w with { NietInSportlink = true },
                sorteerTijd: w => w.AanvangsTijd)).ToList();

    private static VeldbezettingItem NieuweRegel(SportlinkVeldplannerBlok b)
    {
        var n = SportlinkVeldbezettingSamenvoeging.VanBlok(b);
        return new VeldbezettingItem(null, n.Wedstrijd, n.Thuis, n.Uit, n.StartTijd, n.Veld, null, null, n.DuurMinuten, n.Veldafmeting,
            n.Uit, false, SportlinkVeldbezettingSamenvoeging.BronSportlink);
    }
}
