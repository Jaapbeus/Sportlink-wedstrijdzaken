namespace BlazorAdmin.Models;

/// <summary>
/// UI-status van één Sportlink-actie (bezig / melding / fout / dry-run), gedeeld door alle
/// extensie-schermen (#1122). Verving 28 losse <c>Dictionary&lt;long, …&gt;</c>-velden in Dagplanning en
/// vier in Wijzigingsverzoeken die telkens hetzelfde patroon herhaalden, plus zes bijna-identieke
/// "interpreteer het mutatieresultaat"-blokken — die logica staat nu één keer, in
/// <see cref="Verwerk"/>.
/// </summary>
public sealed class SportlinkActieStatus
{
    public const string DryRunTekst = "Dry-run: niets gewijzigd in Sportlink Club — de aanroep is gesimuleerd en gelogd.";
    public const string CodeLockTekst = "Nog niet live bevestigd door Sportlink — deze actie is altijd een simulatie, ook als dry-run uit staat voor deze club.";

    public bool Bezig { get; private set; }
    public string? Melding { get; private set; }
    public bool IsFout { get; private set; }
    public bool IsDryRun { get; private set; }

    /// <summary>#998: dry-run is geen fout maar ook geen echt succes — eigen info-stijl.</summary>
    public string CssKlasse => IsFout ? "text-danger" : IsDryRun ? "text-info" : "text-success";

    public void Start() { Bezig = true; Melding = null; }
    public void Klaar() => Bezig = false;
    public void Fout(string melding) { IsFout = true; IsDryRun = false; Melding = melding; }
    public void Info(string melding) { IsFout = false; Melding = melding; }
    public void Wis() { Melding = null; IsFout = false; IsDryRun = false; }

    /// <summary>
    /// Vertaalt de uitkomst van een Sportlink-mutatie naar één melding. Volgorde is dezelfde als de
    /// audit-bepaling op de server: transportfout → code-lock → dry-run → succes → afgewezen.
    /// Geeft <c>true</c> terug als de actie (echt of gesimuleerd) geslaagd is, zodat de aanroeper
    /// weet of hij de lijst moet verversen.
    /// </summary>
    public bool Verwerk(bool success, string? errorMessage, SportlinkMutatieResultaatDto? data, string succesTekst, string afgewezenTekst)
    {
        if (!success) { Fout(errorMessage ?? "Onbekende fout."); return false; }

        IsDryRun = data?.IsDryRun == true;
        if (data?.IsForcedDryRun == true) { Info(CodeLockTekst); return true; }
        if (data?.IsDryRun == true) { Info(DryRunTekst); return true; }
        if (data?.IsSuccess == true) { Info(succesTekst); return true; }

        var violations = data?.Violations;
        Fout(afgewezenTekst + (violations is { Count: > 0 } ? $": {string.Join(", ", violations)}" : "."));
        return false;
    }
}
