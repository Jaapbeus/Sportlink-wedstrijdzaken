using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner
{
    /// <summary>
    /// Facade — delegeert alle aanroepen naar de specifieke repository-klassen.
    /// Bestaande callers (PlannerService, PlannerFunctions) hoeven niet te worden aangepast. (#474)
    ///
    /// Repository-verdeling:
    ///   PlannerSettingsRepository  — Speeltijden, Velden, Zonsondergang, Seizoen, VoorkeurTijden
    ///   PlannerAvailabilityRepository — VeldBeschikbaarheid, bezettingsqueries
    ///   PlannerMatchRepository     — Wedstrijd zoeken, plannen, herplannen, vervallen markeren
    ///   TeamRulesRepository        — TeamRegels, buffers
    ///   AllstarsTestDataRepository — ALLSTARS testdata, AVG-contacten
    /// </summary>
    public static class PlannerDataAccess
    {
        // ── Settings ──
        public static Task<Speeltijd?> GetSpeeltijdAsync(string leeftijdsCategorie, string? clubCode = null)
            => PlannerSettingsRepository.GetSpeeltijdAsync(leeftijdsCategorie, clubCode);

        public static Task<List<VeldInfo>> GetVeldenAsync(string? clubCode = null)
            => PlannerSettingsRepository.GetVeldenAsync(clubCode);

        public static Task<Dictionary<string, int>> GetVeldenLookupAsync()
            => PlannerSettingsRepository.GetVeldenLookupAsync();

        public static Task<Dictionary<string, Speeltijd>> GetSpeeltijdenLookupAsync(string? clubCode = null)
            => PlannerSettingsRepository.GetSpeeltijdenLookupAsync(clubCode);

        public static Task<Dictionary<string, string>> GetTeamLeeftijdLookupAsync(string clubCode)
            => PlannerSettingsRepository.GetTeamLeeftijdLookupAsync(clubCode);

        public static Task<TimeOnly?> GetSunsetAsync(DateOnly date)
            => PlannerSettingsRepository.GetSunsetAsync(date);

        public static Task PopulateSunsetTableAsync(DateOnly from, DateOnly to)
            => PlannerSettingsRepository.PopulateSunsetTableAsync(from, to);

        public static Task<DateOnly?> GetSeasonEndDateAsync()
            => PlannerSettingsRepository.GetSeasonEndDateAsync();

        public static Task<DateTime?> GetLastSyncTimestampAsync()
            => PlannerSettingsRepository.GetLastSyncTimestampAsync();

        public static Task<Dictionary<string, List<(TimeOnly Tijd, int Prioriteit)>>> GetVoorkeurTijdenLookupAsync(int dagVanWeek, string clubCode)
            => PlannerSettingsRepository.GetVoorkeurTijdenLookupAsync(dagVanWeek, clubCode);

        // ── Availability ──
        public static Task<List<VeldBeschikbaarheidInfo>> GetAvailableFieldsAsync(DateOnly date, string? clubCode = null)
            => PlannerAvailabilityRepository.GetAvailableFieldsAsync(date, clubCode);

        public static Task<List<BestaandeWedstrijd>> GetFieldOccupationsAsync(DateOnly date)
            => PlannerAvailabilityRepository.GetFieldOccupationsAsync(date);

        public static Task<List<BestaandeWedstrijd>> GetFieldOccupationsExcludingAsync(DateOnly date, long excludeWedstrijdcode)
            => PlannerAvailabilityRepository.GetFieldOccupationsExcludingAsync(date, excludeWedstrijdcode);

        public static Task<List<BestaandeWedstrijd>> GetFieldOccupationsExcludingMatchAsync(DateOnly date, string wedstrijdNaam, TimeOnly aanvangsTijd, int veldNummer)
            => PlannerAvailabilityRepository.GetFieldOccupationsExcludingMatchAsync(date, wedstrijdNaam, aanvangsTijd, veldNummer);

        // ── Match ──
        public static Task<List<BestaandeWedstrijd>> GetTeamMatchesOnDateAsync(string teamNaam, DateOnly date)
            => PlannerMatchRepository.GetTeamMatchesOnDateAsync(teamNaam, date);

        public static Task<List<BestaandeWedstrijd>> GetGeplandeWedstrijdenOnlyAsync(DateOnly date)
            => PlannerMatchRepository.GetGeplandeWedstrijdenOnlyAsync(date);

        public static Task<ZoekWedstrijdResponse?> FindMatchAsync(string teamNaam, DateOnly date)
            => PlannerMatchRepository.FindMatchAsync(teamNaam, date);

        public static Task<ZoekWedstrijdResponse?> FindMatchByOpponentAsync(string tegenstander, DateOnly? datum)
            => PlannerMatchRepository.FindMatchByOpponentAsync(tegenstander, datum);

        public static Task<ZoekWedstrijdResponse?> FindMatchByCodeAsync(long wedstrijdcode)
            => PlannerMatchRepository.FindMatchByCodeAsync(wedstrijdcode);

        public static Task<int> SavePlannedMatchAsync(
            DateOnly datum, TimeOnly aanvangsTijd, TimeOnly eindTijd, int veldNummer,
            decimal veldDeelGebruik, string? leeftijdsCategorie, string? teamNaam,
            string? tegenstander, int wedstrijdDuurMinuten, string? aangevraagdDoor,
            string? clubCode = null)
            => PlannerMatchRepository.SavePlannedMatchAsync(datum, aanvangsTijd, eindTijd, veldNummer,
                   veldDeelGebruik, leeftijdsCategorie, teamNaam, tegenstander,
                   wedstrijdDuurMinuten, aangevraagdDoor, clubCode);

        public static Task<int> SaveHerplanVerzoekAsync(
            long wedstrijdcode, string huidigeWedstrijd, DateOnly huidigeDatum,
            TimeOnly huidigeAanvangsTijd, string? huidigeVeldNaam,
            TimeOnly gewensteAanvangsTijd, int? gewenstVeldNummer,
            string? aangevraagdDoor, string? opmerking)
            => PlannerMatchRepository.SaveHerplanVerzoekAsync(wedstrijdcode, huidigeWedstrijd, huidigeDatum,
                   huidigeAanvangsTijd, huidigeVeldNaam, gewensteAanvangsTijd, gewenstVeldNummer,
                   aangevraagdDoor, opmerking);

        public static Task MarkeerVervallenGeplandeWedstrijdenAsync(ILogger log)
            => PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync(log);

        public static Task<bool> TeamExistsAsync(string team)
            => PlannerMatchRepository.TeamExistsAsync(team);

        public static Task<List<TeamScheduleWedstrijd>> GetFutureMatchesForTeamAsync(string team, DateOnly van, DateOnly tot)
            => PlannerMatchRepository.GetFutureMatchesForTeamAsync(team, van, tot);

        // ── Team Rules ──
        public static Task<List<TeamRegel>> GetTeamRulesAsync(string teamNaam, string? clubCode = null)
            => TeamRulesRepository.GetTeamRulesAsync(teamNaam, clubCode);

        public static Task<Dictionary<string, (int bufferVoor, int bufferNa)>> GetAllTeamBuffersAsync(string? clubCode = null)
            => TeamRulesRepository.GetAllTeamBuffersAsync(clubCode);

        // ── ALLSTARS testdata ──
        public static Task<List<VeldInfo>> GetAllstarsVeldenAsync()
            => AllstarsTestDataRepository.GetAllstarsVeldenAsync();

        public static Task<List<WedstrijdRaw>> GetAllMatchesForDatumAsync(DateOnly datum, string clubCode)
            => AllstarsTestDataRepository.GetAllMatchesForDatumAsync(datum, clubCode);

        public static Task<int> UpdateAllstarsMatchAsync(long wedstrijdCode, string nieuweVeld, string nieuweTijd)
            => AllstarsTestDataRepository.UpdateAllstarsMatchAsync(wedstrijdCode, nieuweVeld, nieuweTijd);

        public static Task<TeamleiderContact?> GetTeamleiderContactAsync(string teamNaam)
            => AllstarsTestDataRepository.GetTeamleiderContactAsync(teamNaam);
    }
}
