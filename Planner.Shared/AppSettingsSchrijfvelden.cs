namespace Planner.Shared;

/// <summary>The writable per-club admin settings contract shared by both database tiers.</summary>
public static class AppSettingsSchrijfvelden
{
    public static IReadOnlyList<string> Toegestaan { get; } = Array.AsReadOnly(new[]
    {
        "HerplanDeadlineDagen", "BufferMinuten",
        "PlannerAfzenderNaam", "CoordinatorNaam", "CoordinatorFunctie", "PlannerEmailAdres",
        "Accommodatie", "FetchSchedule", "EmailVoetnoot",
        "AccommodatiePlaats", "AccommodatieLatitude", "AccommodatieLongitude",
        "UseRealtimeApi", "KnvbPdfBijlageIngeschakeld", "KnvbStandaardRegio",
        "SportlinkExtensionEnabled", "SportlinkDryRun", "SportlinkSpelactiviteit", "PdfExportIngeschakeld", "ZekerheidspoortActief", "SeasonStartMonth", "OptimalisatieAanvang",
        "EmailVerwerkingIngeschakeld", "EmailReviewModusActief", "EmailReviewOntvanger"
    });
}
