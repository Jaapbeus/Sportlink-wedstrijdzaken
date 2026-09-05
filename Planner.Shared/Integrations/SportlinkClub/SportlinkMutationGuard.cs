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
    Uitslag
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
