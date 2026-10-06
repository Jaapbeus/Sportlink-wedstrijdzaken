using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Sportlink;
using SportlinkFunction.Infrastructure;

namespace SportlinkFunction.Planner
{
    /// <summary>
    /// SQL Server-tegenhanger van <c>FunctionApp.Postgres/Planner/VeldbezettingSportlinkOverlay.cs</c> (#1563):
    /// veld, starttijd en blokduur uit de Sportlink-veldplanner, met de eigen berekening als terugval. De gedeelde
    /// logica staat in <see cref="VeldplannerOverlayCore"/>.
    /// </summary>
    internal static class VeldbezettingSportlinkOverlay
    {
        internal static async Task<List<VeldbezettingItem>> PasToeAsync(
            List<VeldbezettingItem> items, FunctionContext context, DateOnly datum, string clubCode, ILogger log)
        {
            var koppeling = await VeldplannerOverlayCore.KoppelAsync(items.Select(i => (i.Wedstrijd, (string?)i.AanvangsTijd)).ToList(),
                clubCode, context, SystemUtilities.AppSettings.GetSetting, EgressGuard.ExternalIntegrationsAllowed, datum, log);
            if (koppeling.Count == 0) return items;

            foreach (var (i, b) in koppeling)
            {
                items[i].AanvangsTijd = b.StartTijd;
                items[i].Veld = b.Veld;
                items[i].DuurMinuten = b.DuurMinuten;
                items[i].Veldafmeting = b.Veldafmeting;
            }
            return items.OrderBy(w => VeldplannerOverlayCore.SorteerSleutel(w.AanvangsTijd)).ToList();
        }
    }
}
