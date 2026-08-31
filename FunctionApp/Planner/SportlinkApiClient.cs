using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Planner.Shared;
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
            DateOnly date, ILogger log, string? clubCodeScope = null)
        {
            var useRealtime = SystemUtilities.AppSettings.GetSetting("useRealtimeApi");
            if (useRealtime == "0" || string.Equals(useRealtime, "false", StringComparison.OrdinalIgnoreCase))
            {
                log.LogDebug("SportlinkApiClient: real-time API uitgeschakeld, gebruik DB.");
                return await PlannerDataAccess.GetFieldOccupationsAsync(date, clubCodeScope);
            }

            try
            {
                var apiUrl    = SystemUtilities.AppSettings.GetSetting("sportlinkApiUrl")
                                ?? throw new InvalidOperationException("sportlinkApiUrl niet ingesteld");
                var clientId  = SystemUtilities.AppSettings.GetSetting("sportlinkClientId")
                                ?? throw new InvalidOperationException("sportlinkClientId niet ingesteld");
                var accommodatie = SystemUtilities.AppSettings.GetSetting("accommodatie");
                // De Sportlink-API levert per definitie data van de club achter de clientId;
                // de DB-lookups en planner-slots worden op dezelfde club gescoped (#580).
                var clubCode     = ClubScope.Resolve(clubCodeScope);

                // weekoffset: hoe ver ligt 'date' van vandaag (in volle weken)?
                int weekoffset = (int)Math.Floor(
                    (date.ToDateTime(TimeOnly.MinValue) - DateTime.Today).TotalDays / 7.0);

                var url = $"{apiUrl.TrimEnd('/')}/programma" +
                          $"?clientId={Uri.EscapeDataString(clientId)}" +
                          $"&weekoffset={weekoffset}&aantaldagen=1&aantalregels=10000&uit=NEE";

                // DB-lookups parallel laden met de API-call
                var lookupTask     = LoadLookupsAsync(clubCode);
                var plannerTask    = PlannerDataAccess.GetGeplandeWedstrijdenOnlyAsync(date, clubCode);
                // Trainingsblokken (#679) — ontbreken in de live Sportlink-respons, dus altijd
                // apart uit de DB erbij halen, ook als het real-time API-pad actief is.
                var trainingTask   = PlannerAvailabilityRepository.GetTrainingOccupationsAsync(date, clubCode);
                var apiResponseTask = _http.GetStringAsync(url);

                await Task.WhenAll(lookupTask, plannerTask, trainingTask, apiResponseTask);

                var (veldenLookup, speeltijdenLookup, teamLeeftijdLookup) = await lookupTask;
                var plannerEntries = await plannerTask;
                var trainingEntries = await trainingTask;
                var json = await apiResponseTask;

                var matches = JsonConvert.DeserializeObject<List<SportlinkProgrammaMatch>>(json)
                              ?? new List<SportlinkProgrammaMatch>();

                // Eén projectie voor alle wedstrijden; de matching zelf zit in PlannerShared,
                // zodat de bezetting exact hetzelfde veld kiest als het herplanpad (#707).
                var veldenPerNaam = veldenLookup.Select(kv => ((string?)kv.Key, kv.Value)).ToList();

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

                    // veld → VeldNummer + subpositie via de gedeelde matcher. Hier stond een harde
                    // afkap op zes tekens (m.Veld[..6]), gespiegeld op RTRIM(LEFT(veld, 6)) in SQL.
                    // Die maakte van "veld 10" veld 1: de bezetting van veld 10 landde op veld 1 en
                    // veld 10 leek de hele dag vrij — een tweede wedstrijd kon er bovenop worden
                    // aangeboden. Veldnamen langer dan zes tekens vielen zelfs volledig uit de
                    // bezetting weg, met hetzelfde effect. (#707)
                    var (veldNummer, subpositie) = PlannerShared.ResolveVeld(m.Veld, veldenPerNaam);
                    if (veldNummer == 0) continue;

                    // teamnaam → Speeltijden-sleutel
                    var speeltijdKey = MapTeamNaamToSpeeltijdKey(m.Teamnaam, clubCode, teamLeeftijdLookup);
                    if (speeltijdKey == null || !speeltijdenLookup.TryGetValue(speeltijdKey, out var speeltijd))
                        continue;

                    if (!TimeOnly.TryParse(m.Aanvangstijd, out var aanvang)) continue;

                    var eindTijd     = aanvang.AddMinutes(speeltijd.WedstrijdTotaal);

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
                        VeldSubpositie   = subpositie,
                        // Zonder deze sleutel kan de herplan-exclusie de eigen wedstrijd niet
                        // vinden en valt ze terug op (veld, tijd, naam) — precies wat #707 brak.
                        Wedstrijdcode    = m.Wedstrijdcode,
                        Bron             = "API"
                    });
                }

                // Samenvoegen: API-entries + planner-entries + trainingsblokken, dedupliceren op
                // (VeldNummer, AanvangsTijd, Wedstrijd)
                var combined = apiEntries
                    .Concat(plannerEntries)
                    .Concat(trainingEntries)
                    .GroupBy(w => (w.VeldNummer, w.AanvangsTijd, (w.Wedstrijd ?? "").ToLowerInvariant()))
                    .Select(g => g.OrderBy(w => w.Bron == "API" ? 0 : 1).First())
                    .ToList();

                log.LogInformation(
                    "SportlinkApiClient: {ApiCount} API + {PlannerCount} planner + {TrainingCount} training → {Total} bezettingen voor {Date}",
                    apiEntries.Count, plannerEntries.Count, trainingEntries.Count, combined.Count, date);

                return combined;
            }
            catch (Exception ex)
            {
                log.LogWarning("SportlinkApiClient: API-fout, fallback naar DB. {Message}", ex.Message);
                return await PlannerDataAccess.GetFieldOccupationsAsync(date, clubCodeScope);
            }
        }

        /// <summary>
        /// Identiek aan <see cref="GetFieldOccupationsWithApiAsync"/>, maar zonder de wedstrijd die
        /// herpland wordt. Uitsluiten gebeurt op <c>wedstrijdcode</c> — de enige sleutel die niet
        /// afhangt van hoe een veldnaam gespeld of afgekapt wordt (#574, #707).
        ///
        /// <para>De vorige versie matchte op (veldnummer, aanvangstijd, wedstrijdnaam). Zodra de
        /// veldnaam aan de matcher-kant anders werd opgelost dan aan de bezettingskant — "veld 10"
        /// tegenover "veld 1" — vond dat filter niets meer. De eigen wedstrijd bleef dan als
        /// bezetting staan op een veld waar ze niet speelde, terwijl haar échte veld leeg leek en
        /// dubbel geboekt kon worden.</para>
        /// </summary>
        public static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsExcludingWedstrijdcodeWithApiAsync(
            DateOnly date, long excludeWedstrijdcode, ILogger log, string? clubCodeScope = null)
        {
            var all = await GetFieldOccupationsWithApiAsync(date, log, clubCodeScope);
            var resultaat = PlannerAvailabilityRepository.FilterExcludingWedstrijdcode(all, excludeWedstrijdcode);

            // Niets uitgesloten betekent dat de eigen wedstrijd niet in de bezetting zat. Dat kan
            // legitiem zijn (geen speeltijd geconfigureerd, andere accommodatie), maar het is ook
            // het signaal dat de wedstrijdcode ontbreekt in de bron — dan blijft de eigen wedstrijd
            // als bezetting staan en mist het antwoord alternatieven. Zichtbaar maken, niet slikken.
            if (resultaat.Count == all.Count)
                log.LogWarning(
                    "SportlinkApiClient: geen bezetting uitgesloten voor wedstrijdcode {Code} op {Date} — " +
                    "controleer of de bron een wedstrijdcode meelevert.", excludeWedstrijdcode, date);

            return resultaat;
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
            // Alle drie de lookups op dezelfde club scopen: zonder expliciete clubCode vallen ze
            // terug op de primaire club, waardoor een ALLSTARS-run met productievelden en
            // -speeltijden rekende (#707).
            var veldenTask       = PlannerSettingsRepository.GetVeldenLookupAsync(clubCode);
            var speeltijdenTask  = PlannerSettingsRepository.GetSpeeltijdenLookupAsync(clubCode);
            var teamTask         = PlannerSettingsRepository.GetTeamLeeftijdLookupAsync(clubCode);

            await Task.WhenAll(veldenTask, speeltijdenTask, teamTask);

            return (await veldenTask, await speeltijdenTask, await teamTask);
        }
    }

    // Minimale JSON-mapping voor /programma response
    internal class SportlinkProgrammaMatch
    {
        [JsonProperty("teamnaam")]     public string? Teamnaam      { get; set; }
        [JsonProperty("wedstrijd")]    public string? Wedstrijd     { get; set; }
        // Exacte sleutel voor de herplan-exclusie; null als /programma de code niet meelevert.
        [JsonProperty("wedstrijdcode")] public long?  Wedstrijdcode { get; set; }
        [JsonProperty("aanvangstijd")] public string? Aanvangstijd  { get; set; }
        [JsonProperty("veld")]         public string? Veld          { get; set; }
        [JsonProperty("accommodatie")] public string? Accommodatie  { get; set; }
        [JsonProperty("status")]       public string? Status        { get; set; }
    }
}
