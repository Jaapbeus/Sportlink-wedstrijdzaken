namespace Planner.Shared.Email.Trace;

/// <summary>Wat er in productie met deze mail zou gebeuren (#1583).</summary>
public enum TesterUitkomst
{
    /// <summary>Het antwoord gaat automatisch naar de afzender.</summary>
    AutomatischVerstuurd,

    /// <summary>De zekerheidspoort houdt het antwoord tegen: status Review, een mens kijkt mee.</summary>
    Review,

    /// <summary>Het reply-beleid onderdrukt het antwoord (bijv. een planning die handmatig moet).</summary>
    GeenAntwoord
}

/// <summary>
/// Definitief eindoordeel van de e-mailtester (dry-run), met de actuele instelling van de zekerheidspoort
/// meegewogen. De tekst staat hier, op één plek, zodat beide tiers hem identiek tonen.
/// </summary>
/// <param name="Uitkomst">Machine-leesbare uitkomst.</param>
/// <param name="Titel">Eén regel, definitief geformuleerd.</param>
/// <param name="Toelichting">Waarom dit de uitkomst is.</param>
/// <param name="Waarschuwing"><c>true</c> als de uitkomst een risico heeft (onzeker oordeel met poort uit).</param>
/// <param name="ConceptLabel">Kop boven het voorbeeld-antwoord: wat dat antwoord in deze situatie is.</param>
public sealed record TesterEindoordeel(
    TesterUitkomst Uitkomst, string Titel, string Toelichting, bool Waarschuwing, string ConceptLabel)
{
    /// <summary>
    /// Zelfde volgorde als de productieverwerking (<c>EmailReplyPolicyService</c>): eerst het reply-beleid, daarna de
    /// zekerheidspoort. <paramref name="poortActief"/> is de actuele clubinstelling <c>ZekerheidspoortActief</c>.
    /// </summary>
    public static TesterEindoordeel Bepaal(bool replyMoetVersturen, string? replyReden, bool poortActief, bool isZeker)
    {
        if (!replyMoetVersturen)
            return new TesterEindoordeel(
                TesterUitkomst.GeenAntwoord,
                "Er wordt geen automatisch antwoord verstuurd",
                string.IsNullOrWhiteSpace(replyReden)
                    ? "Het reply-beleid onderdrukt het antwoord; de coördinator plant handmatig."
                    : $"Het reply-beleid onderdrukt het antwoord: {replyReden}. De zekerheidspoort speelt hier geen rol.",
                false,
                "Voorbeeld-antwoord (wordt in dit geval niet verstuurd)");

        if (isZeker)
            return new TesterEindoordeel(
                TesterUitkomst.AutomatischVerstuurd,
                "Wordt automatisch verstuurd",
                "Alle beslissingen zijn zeker; het antwoord gaat zonder review naar de afzender.",
                false,
                "Antwoord dat zou worden verstuurd");

        return poortActief
            ? new TesterEindoordeel(
                TesterUitkomst.Review,
                "Gaat naar Review — er wordt géén antwoord verstuurd",
                "Het oordeel is onzeker en de zekerheidspoort staat aan: de mail krijgt de status Review en een beheerder beoordeelt hem. De afzender ontvangt niets automatisch.",
                false,
                "Concept-antwoord (alleen zichtbaar bij review, wordt niet naar de afzender verstuurd)")
            : new TesterEindoordeel(
                TesterUitkomst.AutomatischVerstuurd,
                "Wordt automatisch verstuurd — maar het oordeel is onzeker",
                "De zekerheidspoort staat uit (Instellingen), dus ook een onzeker antwoord gaat zonder review naar de afzender. Zet de poort aan om dit naar Review te laten gaan.",
                true,
                "Antwoord dat zou worden verstuurd (let op: onzeker)");
    }
}
