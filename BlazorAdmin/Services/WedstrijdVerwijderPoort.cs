using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Poort vóór het verwijderen van een zojuist aangemaakte oefenwedstrijd uit Sportlink (#1440).
/// Los van de Razor-pagina, zodat de regels testbaar zijn:
/// <list type="number">
/// <item>De knop verschijnt alleen op het resultaat van een ECHTE aanmaak (geen dry-run) met een
///   <c>PublicMatchId</c> — dan is zeker dat het een door deze app aangemaakte clubwedstrijd is.</item>
/// <item>Verwijderen gebeurt nooit zonder expliciete bevestiging, ook niet als dry-run aan staat:
///   verwijderen is mogelijk niet terug te draaien.</item>
/// <item>Er loopt hooguit één verwijderaanvraag tegelijk; na een echte, geslaagde verwijdering
///   verdwijnt de knop.</item>
/// </list>
/// </summary>
public sealed class WedstrijdVerwijderPoort
{
    public enum Stap { Gereed, Bevestigen, Bezig, Verwijderd }

    public Stap Huidig { get; private set; } = Stap.Gereed;

    /// <summary>Alleen een echte, geslaagde aanmaak met een <c>PublicMatchId</c> mag een verwijderknop krijgen.</summary>
    public static bool MagAanbieden(SportlinkMutatieResultaatDto? aanmaakResultaat)
        => aanmaakResultaat is { IsSuccess: true, IsDryRun: false, IsForcedDryRun: false, PublicMatchId: { Length: > 0 } };

    /// <summary>Is de knop "Verwijderen" zichtbaar voor dit aanmaakresultaat?</summary>
    public bool ToonKnop(SportlinkMutatieResultaatDto? aanmaakResultaat)
        => MagAanbieden(aanmaakResultaat) && Huidig == Stap.Gereed;

    /// <summary>Klik op "Wedstrijd verwijderen": altijd eerst naar de bevestigstap, nooit direct versturen.</summary>
    public void VraagAan()
    {
        if (Huidig == Stap.Gereed) Huidig = Stap.Bevestigen;
    }

    /// <summary>Klik op "Ja, verwijderen". Alleen vanuit de bevestigstap; een tweede klik doet niets.</summary>
    public bool Bevestig()
    {
        if (Huidig != Stap.Bevestigen) return false;
        Huidig = Stap.Bezig;
        return true;
    }

    public void Annuleer()
    {
        if (Huidig == Stap.Bevestigen) Huidig = Stap.Gereed;
    }

    /// <summary>
    /// De aanvraag is afgerond. Alleen een echte (niet gesimuleerde), geslaagde verwijdering maakt de
    /// poort definitief dicht; na een simulatie of fout kan het opnieuw.
    /// </summary>
    public void Klaar(SportlinkMutatieResultaatDto? resultaat)
        => Huidig = resultaat is { IsSuccess: true, IsDryRun: false } ? Stap.Verwijderd : Stap.Gereed;

    /// <summary>Nieuw aanmaakresultaat (of leegmaken): poort terug naar de beginstand.</summary>
    public void Reset() => Huidig = Stap.Gereed;
}
