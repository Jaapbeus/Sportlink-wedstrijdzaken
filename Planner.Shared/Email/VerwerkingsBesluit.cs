namespace Planner.Shared.Email;

/// <summary>
/// Wat er met een inkomend bericht moet gebeuren op basis van een eerdere verwerking. Sinds #1650
/// eenmalig hier in plaats van per tier: het besluit kent geen databasetype.
/// </summary>
public enum VerwerkingsBesluit
{
    /// <summary>Nog niet eerder gezien — nieuwe verwerkingsrij aanmaken.</summary>
    NieuweVerwerking,

    /// <summary>Eerdere poging is niet afgerond — bestaande rij hergebruiken en opnieuw verwerken.</summary>
    HerhaalVerwerking,

    /// <summary>Definitief afgehandeld — niets meer doen (en dus zeker niet opnieuw antwoorden).</summary>
    OverslaanAlAfgerond,

    /// <summary>Te vaak mislukt — opgeven zodat het bericht de wachtrij niet blijft blokkeren.</summary>
    OpgevenNaMaxPogingen,

    /// <summary>
    /// Er is een verzendpoging vastgelegd waarvan de uitkomst onbekend is (#716). Niet opnieuw
    /// versturen — het eerste antwoord kan de deur al uit zijn — maar ter beoordeling neerleggen.
    /// </summary>
    OnbeslistNaVerzendPoging
}

/// <summary>
/// Tier-onafhankelijke kern van het idempotentiebesluit (#712, gedeeld sinds #1650). De tiers
/// bepalen zelf of een stand definitief is (dat hangt aan hun eigen statusenum) en geven de
/// uitkomst hier als <c>bool</c> door; de volgorde van de regels staat alleen hier.
/// </summary>
public static class VerwerkingsBesluitRegels
{
    /// <summary>
    /// Maximaal aantal verwerkingspogingen per bericht. Drie is genoeg om tijdelijke fouten (Graph
    /// 429/503, een net gepauzeerde database, een time-out) uit te zitten, en laag genoeg dat een
    /// structurele fout de wachtrij niet lang bezet houdt: de poll pakt de 10 oudste ongelezen
    /// berichten, dus tien blijvend falende berichten zouden anders alle nieuwe post tegenhouden.
    /// </summary>
    public const int MaxPogingen = 3;

    /// <summary>Besluit voor een bericht waarvan al een verwerkingsrij bestaat.</summary>
    public static VerwerkingsBesluit BepaalVoorBestaandeRij(bool isDefinitief, bool verzendPogingOnbeslist, int pogingen)
    {
        if (isDefinitief)
            return VerwerkingsBesluit.OverslaanAlAfgerond;

        // Vóór de pogingengrens: een onbekende verzenduitkomst is geen "mislukte poging" die je nog een
        // keer mag proberen. Zou dit ná de grens staan, dan zou een bericht met twee eerdere pogingen
        // alsnog opnieuw verstuurd worden — precies wat #716 moet voorkomen.
        if (verzendPogingOnbeslist)
            return VerwerkingsBesluit.OnbeslistNaVerzendPoging;

        return pogingen >= MaxPogingen
            ? VerwerkingsBesluit.OpgevenNaMaxPogingen
            : VerwerkingsBesluit.HerhaalVerwerking;
    }
}

/// <summary>
/// Typevrije samenvatting van een bestaande verwerkingsrij, zoals de gedeelde afhandeling
/// (<see cref="EmailVerwerkingsAfhandeling"/>) die nodig heeft. De tier vult hem uit zijn eigen
/// standrecord, inclusief het al genomen <see cref="Besluit"/>.
/// </summary>
public sealed record EmailVerwerkingStandInfo(int VerwerkingId, string Status, int Pogingen, VerwerkingsBesluit Besluit);
