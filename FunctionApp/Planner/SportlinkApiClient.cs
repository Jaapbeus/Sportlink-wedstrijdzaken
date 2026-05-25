using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace SportlinkFunction.Planner
{
    // Real-time veldbezetting via Sportlink /programma endpoint.
    // Valt terug op de database als de API onbereikbaar is of UseRealtimeApi=false.
    public static class SportlinkApiClient
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

        public static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsWithApiAsync(
            DateOnly date, ILogger log)
        {
            var useRealtime = SystemUtilities.AppSettings.GetSetting("useRealtimeApi");
            if (useRealtime == "0" || string.Equals(useRealtime, "false", StringComparison.OrdinalIgnoreCase))
            {
                log.LogDebug("SportlinkApiClient: real-time API uitgeschakeld, gebruik DB.");
                return await PlannerDataAccess.GetFieldOccupationsAsync(date);
            }

            try
            {
                var apiUrl    = SystemUtilities.AppSettings.GetSetting("sportlinkApiUrl")
                                ?? throw new InvalidOperationException("sportlinkApiUrl niet ingesteld");
                var clientId  = SystemUtilities.AppSettings.GetSetting("sportlinkClientId")
                                ?? throw new InvalidOperationException("sportlinkClientId niet ingesteld");
                var accommodatie = SystemUtilities.AppSettings.GetSetting("accommodatie");
                var clubCode     = SystemUtilities.AppSettings.GetSetting("clubCode") ?? "";

                // weekoffset: hoe ver ligt 'date' van vandaag (in volle weken)?
                int weekoffset = (int)Math.Floor(
                    (date.ToDateTime(TimeOnly.MinValue) - DateTime.Today).TotalDays / 7.0);

                var url = $"{apiUrl.TrimEnd('/')}/programma" +
                          $"?clientId={Uri.EscapeDataString(clientId)}" +
                          $"&weekoffset={weekoffset}&aantaldagen=1&aantalregels=10000&uit=NEE";

                // DB-lookups parallel laden met de API-call
                var lookupTask     = LoadLookupsAsync(clubCode);
                var plannerTask    = PlannerDataAccess.GetGeplandeWedstrijdenOnlyAsync(date);
                var apiResponseTask = _http.GetStringAsync(url);

                await Task.WhenAll(lookupTask, plannerTask, apiResponseTask);

                var (veldenLookup, speeltijdenLookup, teamLeeftijdLookup) = await lookupTask;
                var plannerEntries = await plannerTask;
                var json = await apiResponseTask;

                var matches = JsonConvert.DeserializeObject<List<SportlinkProgrammaMatch>>(json)
                              ?? new List<SportlinkProgrammaMatch>();

                var apiEntries = new List<BestaandeWedstrijd>();
                foreach (var m in matches)
                {
                    // Alleen thuiswedstrijden op eigen accommodatie
                    if (string.IsNullOrEmpty(m.Accommodatie)) continue;
                    if (accommodatie != null &&
                        !m.Accommodatie.Contains(accommodatie, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.Equals(m.Status, "Afgelast", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(m.Aanvangstijd) || string.IsNullOrEmpty(m.Veld)) continue;

                    // veld → VeldNummer via lookup (RTRIM(LEFT(veld, 6)) = VeldNaam)
                    var veldKey = m.Veld.Length >= 6 ? m.Veld[..6].TrimEnd() : m.Veld.TrimEnd();
                    if (!veldenLookup.TryGetValue(veldKey, out var veldNummer)) continue;

                    // teamnaam → Speeltijden-sleutel
                    var speeltijdKey = MapTeamNaamToSpeeltijdKey(m.Teamnaam, clubCode, teamLeeftijdLookup);
                    if (speeltijdKey == null || !speeltijdenLookup.TryGetValue(speeltijdKey, out var speeltijd))
                        continue;

                    if (!TimeOnly.TryParse(m.Aanvangstijd, out var aanvang)) continue;

                    var eindTijd     = aanvang.AddMinutes(speeltijd.WedstrijdTotaal);
                    var subpositie   = m.Veld.Length > 6 ? m.Veld[6..].Trim() : null;

                    apiEntries.Add(new BestaandeWedstrijd
                    {
                        Datum            = date,
                        AanvangsTijd     = aanvang,
                        EindTijd         = eindTijd,
                        VeldNummer       = veldNummer,
                        VeldDeelGebruik  = speeltijd.Veldafmeting,
                        LeeftijdsCategorie = speeltijdKey,
                        TeamNaam         = m.Teamnaam,
                        Wedstrijd        = m.Wedstrijd,
                        VeldSubpositie   = string.IsNullOrEmpty(subpositie) ? null : subpositie,
                        Bron             = "API"
                    });
                }

                // Samenvoegen: API-entries + planner-entries, dedupliceren op (VeldNummer, AanvangsTijd, Wedstrijd)
                var combined = apiEntries
                    .Concat(plannerEntries)
                    .GroupBy(w => (w.VeldNummer, w.AanvangsTijd, (w.Wedstrijd ?? "").ToLowerInvariant()))
                    .Select(g => g.OrderBy(w => w.Bron == "API" ? 0 : 1).First())
                    .ToList();

                log.LogInformation(
                    "SportlinkApiClient: {ApiCount} API + {PlannerCount} planner → {Total} bezettingen voor {Date}",
                    apiEntries.Count, plannerEntries.Count, combined.Count, date);

                return combined;
            }
            catch (Exception ex)
            {
                log.LogWarning("SportlinkApiClient: API-fout, fallback naar DB. {Message}", ex.Message);
                return await PlannerDataAccess.GetFieldOccupationsAsync(date);
            }
        }

        // Identiek aan GetFieldOccupationsWithApiAsync maar filtert één wedstrijd eruit (voor herplan).
        public static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsExcludingMatchWithApiAsync(
            DateOnly date, string wedstrijdNaam, TimeOnly aanvangsTijd, int veldNummer, ILogger log)
        {
            var all = await GetFieldOccupationsWithApiAsync(date, log);
            return all.Where(o =>
                !(o.VeldNummer == veldNummer &&
                  o.AanvangsTijd == aanvangsTijd &&
                  o.Wedstrijd != null &&
                  o.Wedstrijd.Trim() == wedstrijdNaam.Trim())
            ).ToList();
        }

        private static string? MapTeamNaamToSpeeltijdKey(
            string? teamnaam, string clubCode,
            Dictionary<string, string> teamLeeftijdLookup)
        {
            if (string.IsNullOrEmpty(teamnaam)) return null;

            // G-voetbal: teamnaam = "<ClubCode> G1", "<ClubCode> G2" etc.
            if (!string.IsNullOrEmpty(clubCode) &&
                teamnaam.StartsWith(clubCode + " G", StringComparison.OrdinalIgnoreCase) &&
                teamnaam.Length > clubCode.Length + 2 &&
                char.IsDigit(teamnaam[clubCode.Length + 2]))
                return "G";

            // Opzoeken in his.teams lookup (leeftijdscategorie al gemapt naar Speeltijden-sleutel)
            return teamLeeftijdLookup.TryGetValue(teamnaam, out var key) ? key : null;
        }

        private static async Task<(
            Dictionary<string, int> veldenLookup,
            Dictionary<string, Speeltijd> speeltijdenLookup,
            Dictionary<string, string> teamLeeftijdLookup)> LoadLookupsAsync(string clubCode)
        {
            var veldenTask       = PlannerDataAccess.GetVeldenLookupAsync();
            var speeltijdenTask  = PlannerDataAccess.GetSpeeltijdenLookupAsync();
            var teamTask         = PlannerDataAccess.GetTeamLeeftijdLookupAsync(clubCode);

            await Task.WhenAll(veldenTask, speeltijdenTask, teamTask);

            return (await veldenTask, await speeltijdenTask, await teamTask);
        }
    }

    // Minimale JSON-mapping voor /programma response
    internal class SportlinkProgrammaMatch
    {
        [JsonProperty("teamnaam")]     public string? Teamnaam      { get; set; }
        [JsonProperty("wedstrijd")]    public string? Wedstrijd     { get; set; }
        [JsonProperty("aanvangstijd")] public string? Aanvangstijd  { get; set; }
        [JsonProperty("veld")]         public string? Veld          { get; set; }
        [JsonProperty("accommodatie")] public string? Accommodatie  { get; set; }
        [JsonProperty("status")]       public string? Status        { get; set; }
    }
}
