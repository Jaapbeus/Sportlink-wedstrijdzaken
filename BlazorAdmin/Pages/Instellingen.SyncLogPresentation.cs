using BlazorAdmin.Models;

namespace BlazorAdmin.Pages;

public partial class Instellingen
{
    internal static DateTime NaarLokaleTijd(DateTime tijdstip)
    {
        var utc = tijdstip.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(tijdstip, DateTimeKind.Utc)
            : tijdstip.ToUniversalTime();
        return utc.ToLocalTime();
    }

    internal static string? ToonDuur(DateTime gestart, DateTime? afgerond)
    {
        if (afgerond == null) return null;
        var duur = afgerond.Value - gestart;
        return duur.TotalMinutes >= 1 ? $"{(int)duur.TotalMinutes} min {duur.Seconds} sec" : $"{Math.Max(0, duur.Seconds)} sec";
    }

    internal static string SyncStatusTekst(string? status) => status switch
    {
        "pending" => "Wacht",
        "running" => "Bezig",
        "succeeded" => "Gelukt",
        "partial" => "Deels mislukt",
        "failed" => "Mislukt",
        "skipped" => "Overgeslagen",
        _ => "Onbekend"
    };

    internal static string SyncStatusKleur(string? status) => status switch
    {
        "succeeded" => "bg-success",
        "running" => "bg-primary",
        "pending" => "bg-secondary",
        "partial" => "bg-warning text-dark",
        "failed" => "bg-danger",
        "skipped" => "bg-light text-dark",
        _ => "bg-secondary"
    };

    internal static string SyncFaseTekst(string? fase) => fase switch
    {
        "teams" => "Teams",
        "programma" => "Programma",
        "uitslagen" => "Uitslagen",
        "matchdetails" => "Wedstrijddetails",
        "planner-view" => "Plannerweergave",
        "canonicalisatie" => "Teamcanonicalisatie",
        "reconciliatie" => "Reconciliatie",
        "configuratie" => "Configuratie",
        _ => "Synchronisatiefase"
    };

    private static string? SyncRedenTekst(string? reden) => reden switch
    {
        "operation_failed" => "De fase kon niet worden uitgevoerd.",
        "partial_failure" => "Een deel van de gegevens kon niet worden verwerkt.",
        "configuration_missing" => "Vereiste configuratie ontbreekt.",
        "egress_blocked" => "Externe verbinding is geblokkeerd.",
        "programme_truncated" => "Het programma is niet volledig opgehaald.",
        "reconciliation_skipped" => "Reconciliatie is overgeslagen.",
        "source_failed" => "De brongegevens konden niet worden opgehaald.",
        "not_applicable" => "Deze fase is voor deze databasetier niet van toepassing.",
        _ => null
    };

    internal static string SyncFaseResultaat(SyncLogEntryDto phase)
    {
        var aantallen = new List<string>();
        if (phase.Fetched.HasValue) aantallen.Add($"opgehaald {phase.Fetched}");
        if (phase.Added.HasValue) aantallen.Add($"toegevoegd {phase.Added}");
        if (phase.Updated.HasValue) aantallen.Add($"bijgewerkt {phase.Updated}");
        if (phase.Failed.HasValue) aantallen.Add($"mislukt {phase.Failed}");

        var resultaat = string.Join(" · ", aantallen);
        var reden = SyncRedenTekst(phase.ReasonCode);
        if (string.IsNullOrWhiteSpace(resultaat)) return reden ?? "—";
        return reden == null ? resultaat : $"{resultaat} · {reden}";
    }
}
