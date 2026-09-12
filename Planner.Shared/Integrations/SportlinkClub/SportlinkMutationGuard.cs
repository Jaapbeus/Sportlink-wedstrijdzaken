namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Type van Sportlink-mutatie waarvoor permissies gecontroleerd moeten worden.
/// </summary>
public enum SportlinkMutationSoort
{
    Kleedkamers,
    Veld,
    VeldSidePanel,
    Officials,
    Uitslag,

    /// <summary>
    /// Wijzigingsverzoek datum/tijd/accommodatie (#995, epic #986) — de enige mutatiesoort die een
    /// ECHTE tegenstander raakt (Sportlink stuurt bij bevestiging een goedkeuringsverzoek naar de
    /// tegenstander). Deze app bouwt uitsluitend stap 1 (valideren), nooit stap 2 (bevestigen) — zie
    /// <see cref="SportlinkMatchChangeValidatie"/> en de forceDryRun-lock op
    /// <c>SportlinkClubClient.RequestMatchChangeAsync</c>.
    /// </summary>
    DatumTijdAccommodatie
}

/// <summary>
/// Resultaat van een mutatie-permissie-check.
/// </summary>
public sealed record SportlinkMutationGuardResult(bool IsToegstaan, string? Reden)
{
    public static SportlinkMutationGuardResult Toegestaan() => new(true, null);
    public static SportlinkMutationGuardResult Geblokkeerd(string reden) => new(false, reden);
}

/// <summary>
/// Guardrail-logica voor Sportlink-mutaties.
/// Controleert of een bepaalde actie op een wedstrijd toegestaan is.
/// </summary>
public static class SportlinkMutationGuard
{
    /// <summary>
    /// Controleert of een bepaalde mutatie op deze wedstrijd toegestaan is.
    /// Enkel thuiswedstrijden mogen via de extension gewijzigd worden.
    /// </summary>
    public static SportlinkMutationGuardResult MagMuteren(SportlinkMatch match, SportlinkMutationSoort soort)
    {
        if (!match.IsHomeMatch)
            return SportlinkMutationGuardResult.Geblokkeerd(
                "Alleen thuiswedstrijden mogen via de extension gewijzigd worden (IsHomeMatch=false).");

        // #998: hard blokkeren op IsCanceledMatch/IsConceptMatch — onomstreden gevallen waarin een
        // mutatie nooit zinvol is. MatchStatus wordt BEWUST niet hard afgedwongen (bijv. op
        // "SCHEDULED"): de eigenaar koos ervoor eerst een seizoen auditdata te verzamelen (zie de
        // uitgebreide WaardeVoor-logging in SportlinkMatchFunction) voordat die waarde een
        // blokkade wordt.
        if (match.IsCanceledMatch)
            return SportlinkMutationGuardResult.Geblokkeerd("Wedstrijd is afgelast (IsCanceledMatch=true).");
        if (match.IsConceptMatch)
            return SportlinkMutationGuardResult.Geblokkeerd("Wedstrijd is nog een concept (IsConceptMatch=true).");

        // #995: er bestaat geen SportlinkMatch.IsXxxAllowed-vlag voor een datum/tijd/accommodatie-
        // wijzigingsverzoek. TODO: exacte permissievlag voor datum/tijd-wijziging is niet
        // vastgesteld — vertrouw voorlopig op IsHomeMatch (al gecontroleerd hierboven) + de
        // generieke checks (niet afgelast, niet concept). Uitwedstrijden zijn al uitgesloten door
        // de IsHomeMatch-check aan het begin van deze methode — het issue beschrijft alleen een
        // verzoek vanuit de thuisclub.
        if (soort == SportlinkMutationSoort.DatumTijdAccommodatie)
            return SportlinkMutationGuardResult.Toegestaan();

        var toegestaan = soort switch
        {
            SportlinkMutationSoort.Kleedkamers => match.IsAssignDressingRoomsAllowed,
            SportlinkMutationSoort.Veld => match.IsEditFieldAllowed,
            SportlinkMutationSoort.VeldSidePanel => match.IsEditFieldSidePanelAllowed,
            SportlinkMutationSoort.Officials => match.IsAssignOfficialsAllowed,
            SportlinkMutationSoort.Uitslag => match.IsAddScoreAllowed,
            _ => false
        };

        return toegestaan
            ? new SportlinkMutationGuardResult(true, null)
            : SportlinkMutationGuardResult.Geblokkeerd(
                $"Sportlink staat deze actie niet toe voor deze wedstrijd ({soort}).");
    }
}
