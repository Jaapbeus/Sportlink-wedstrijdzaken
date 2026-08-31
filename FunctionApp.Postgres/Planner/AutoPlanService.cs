using Planner.Shared;

namespace FunctionApp.Postgres.Planner;

internal sealed record VeldbezettingItem(
    long? WedstrijdCode, string Wedstrijd, string TeamNaam, string? Uitteam,
    string? AanvangsTijd, string? Veld, string? Competitiesoort, string? LeeftijdsCategorie,
    int DuurMinuten, decimal Veldafmeting);

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/Services/AutoPlanService.cs</c> (#888).
/// Alleen <see cref="VeldbezettingAsync"/> is vertaald (de "lichtgewicht weergave zonder
/// FieldScheduler-berekening", #566) — nodig voor <c>GET /api/planner/veldbezetting</c>. De
/// FieldScheduler-engine zelf (<c>AutoPlanAsync</c>/<c>AutoPlanToepassenAsync</c>, de eigenlijke
/// dagplanning-optimalisatie) is niet vertaald; dat is een aanzienlijk grotere, apart te
/// verifiëren stap (regels → voorkeuren → defaults-rangorde, #666) die buiten deze eerste
/// #888-ronde valt.
/// </summary>
internal static class AutoPlanService
{
    internal static async Task<List<VeldbezettingItem>> VeldbezettingAsync(
        string connectionString, DateOnly datum, string clubCode)
    {
        bool isAllstars = clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase);
        var wedstrijden = await AllstarsTestDataRepository.GetAllMatchesForDatumAsync(connectionString, datum, clubCode);
        var speeltijden = await GetSpeeltijdenMetTerugvalAsync(connectionString, clubCode);

        return wedstrijden
            .Select(w =>
            {
                var leeftijd = !string.IsNullOrWhiteSpace(w.LeeftijdsCategorie)
                    ? w.LeeftijdsCategorie
                    : (isAllstars ? ExtractLeeftijdFromTeamNaam(w.TeamNaam) ?? "" : "");
                speeltijden.TryGetValue(leeftijd, out var speeltijdInfo);

                return new VeldbezettingItem(
                    WedstrijdCode: w.WedstrijdCode,
                    Wedstrijd: w.Wedstrijd,
                    TeamNaam: w.TeamNaam,
                    Uitteam: w.Uitteam,
                    AanvangsTijd: w.AanvangsTijd,
                    Veld: w.Veld,
                    Competitiesoort: w.Competitiesoort,
                    LeeftijdsCategorie: w.LeeftijdsCategorie,
                    DuurMinuten: speeltijdInfo?.WedstrijdTotaal ?? 0,
                    Veldafmeting: speeltijdInfo?.Veldafmeting ?? 1.00m);
            })
            .OrderBy(w => string.IsNullOrWhiteSpace(w.AanvangsTijd) ? "99:99" : w.AanvangsTijd)
            .ToList();
    }

    private static async Task<Dictionary<string, Speeltijd>> GetSpeeltijdenMetTerugvalAsync(
        string connectionString, string clubCode)
    {
        var eigen = await PlannerSettingsRepository.GetSpeeltijdenLookupAsync(connectionString, clubCode);
        if (eigen.Count > 0) return eigen;

        var primair = PostgresAppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in public.appsettings");
        return await PlannerSettingsRepository.GetSpeeltijdenLookupAsync(connectionString, primair);
    }

    private static string? ExtractLeeftijdFromTeamNaam(string? teamNaam)
    {
        if (string.IsNullOrWhiteSpace(teamNaam)) return null;
        var parts = teamNaam.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var second = parts[1];
        var hyphenIdx = second.IndexOf('-');
        if (hyphenIdx > 0) second = second[..hyphenIdx];
        return second.ToUpperInvariant() switch
        {
            "HEREN" => "1-99", "DAMES" => "VR", "VROUWEN" => "VR",
            _ => string.IsNullOrWhiteSpace(second) ? null : second
        };
    }
}
