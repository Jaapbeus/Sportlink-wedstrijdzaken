using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Planner.Shared.Planning;
using SportlinkFunction.Infrastructure;

namespace SportlinkFunction.Planner
{
    /// <summary>
    /// SQL Server-tegenhanger van <c>FunctionApp.Postgres/Planner/VeldbezettingSportlinkOverlay.cs</c> (#1563, #1582):
    /// Sportlink is leidend voor veld, starttijd en blokduur; een Sportlink-blok zonder eigen regel wordt zelf een regel
    /// en een eigen regel die Sportlink niet kent krijgt de markering "niet in Sportlink". De gedeelde logica staat in
    /// <see cref="SportlinkVeldbezettingSamenvoeging"/> en <see cref="VeldplannerOverlayCore"/>.
    /// </summary>
    internal static class VeldbezettingSportlinkOverlay
    {
        internal static async Task<List<VeldbezettingItem>> PasToeAsync(
            List<VeldbezettingItem> items, FunctionContext context, DateOnly datum, string clubCode, ILogger log)
            => (await VeldplannerOverlayCore.SamenvoegAsync<VeldbezettingItem>(
                    items, clubCode, context, SystemUtilities.AppSettings.GetSetting, EgressGuard.ExternalIntegrationsAllowed, datum, log,
                    sleutel: i => (i.Wedstrijd, (string?)i.AanvangsTijd),
                    overschrijf: (w, b) =>
                    {
                        w.AanvangsTijd = b.StartTijd;
                        w.Veld = b.Veld;
                        w.DuurMinuten = b.DuurMinuten;
                        w.Veldafmeting = b.Veldafmeting;
                        w.Bron = SportlinkVeldbezettingSamenvoeging.BronSportlink;
                        return w;
                    },
                    nieuw: NieuweRegel,
                    nietInSportlink: w => { w.NietInSportlink = true; return w; },
                    sorteerTijd: w => w.AanvangsTijd)).ToList();

        private static VeldbezettingItem NieuweRegel(SportlinkVeldplannerBlok b)
        {
            var n = SportlinkVeldbezettingSamenvoeging.VanBlok(b);
            return new VeldbezettingItem
            {
                Wedstrijd = n.Wedstrijd, TeamNaam = n.Thuis, Uitteam = n.Uit, Tegenstander = n.Uit, AanvangsTijd = n.StartTijd,
                Veld = n.Veld, DuurMinuten = n.DuurMinuten, Veldafmeting = n.Veldafmeting, Bron = SportlinkVeldbezettingSamenvoeging.BronSportlink
            };
        }
    }
}
