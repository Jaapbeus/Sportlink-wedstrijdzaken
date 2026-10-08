using System.Globalization;

namespace Planner.Shared;

/// <summary>
/// De ene plek voor de vertaling van een dagdeel ("ochtend", "middag", "avond") naar een tijdvenster, en
/// voor de zinnen waarmee een antwoord aangeeft welk dagdeel is gecontroleerd (#1587).
/// <para>
/// Tier-onafhankelijk: de venstergrenzen stonden woordelijk in beide <c>AvailabilityService</c>-kopieën
/// (SQL Server- én Postgres-tier); beide delegeren nu hierheen. Een afwijkende grens op één tier zou een
/// antwoord opleveren dat een ander tijdvenster noemt dan het venster dat is gecontroleerd.
/// </para>
/// <para>
/// Eigenaarsbesluit bij #1587: een antwoord op een verzoek met een dagdeel vermeldt welk dagdeel en welk
/// tijdvenster is gecontroleerd, en noemt géén alternatieven uit andere dagdelen — zodat duidelijk is dat de
/// andere dagdelen niet zijn beoordeeld.
/// </para>
/// </summary>
public static class DagdeelVenster
{
    public const string Ochtend = "ochtend";
    public const string Middag = "middag";
    public const string Avond = "avond";

    private static readonly TimeOnly OchtendVan = new(8, 30);
    private static readonly TimeOnly OchtendTot = new(12, 0);
    private static readonly TimeOnly MiddagTot = new(17, 0);
    private static readonly TimeOnly AvondTot = new(22, 0);

    /// <summary>
    /// Brengt een vrije waarde (AI-uitvoer of requestveld) terug tot "ochtend", "middag" of "avond";
    /// null bij leeg of onbekend. Hoofdletters en omringende spaties maken niet uit.
    /// </summary>
    public static string? Normaliseer(string? waarde)
    {
        if (string.IsNullOrWhiteSpace(waarde)) return null;
        return waarde.Trim().ToLowerInvariant() switch
        {
            Ochtend => Ochtend,
            Middag => Middag,
            Avond => Avond,
            _ => null
        };
    }

    /// <summary>Het tijdvenster van een dagdeel, of null bij een leeg of onbekend dagdeel.</summary>
    public static (TimeOnly Van, TimeOnly Tot)? ZoekVenster(string? dagdeel) => Normaliseer(dagdeel) switch
    {
        Ochtend => (OchtendVan, OchtendTot),
        Middag => (OchtendTot, MiddagTot),
        Avond => (MiddagTot, AvondTot),
        _ => null
    };

    /// <summary>
    /// Het tijdvenster van een dagdeel; bij een leeg of onbekend dagdeel de meegegeven standaardwaarden
    /// (alleen de standaardwaarden verschillen per aanroeper).
    /// </summary>
    public static (TimeOnly Van, TimeOnly Tot) Bepaal(string? dagdeel, TimeOnly standaardVan, TimeOnly standaardTot)
        => ZoekVenster(dagdeel) ?? (standaardVan, standaardTot);

    /// <summary>"de ochtend (08:30 - 12:00)", of null bij een leeg of onbekend dagdeel.</summary>
    public static string? Omschrijving(string? dagdeel)
    {
        var naam = Normaliseer(dagdeel);
        var venster = ZoekVenster(dagdeel);
        if (naam == null || venster == null) return null;
        return $"de {naam} ({Tijd(venster.Value.Van)} - {Tijd(venster.Value.Tot)})";
    }

    /// <summary>
    /// Afsluitende zin voor een antwoord waarin de gecontroleerde vensters of tijden staan: benoemt het
    /// gecontroleerde dagdeel en dat andere dagdelen niet zijn meegenomen. Leeg bij een leeg dagdeel.
    /// </summary>
    public static string ControleZin(string? dagdeel)
    {
        var omschrijving = Omschrijving(dagdeel);
        return omschrijving == null
            ? ""
            : $"Let op: we hebben alleen {omschrijving} gecontroleerd; andere dagdelen zijn niet meegenomen in dit antwoord.";
    }

    /// <summary>
    /// Deelzin voor "er is in dit dagdeel niets vrij", bijvoorbeeld "in de ochtend (08:30 - 12:00) is helaas
    /// niets beschikbaar". Zonder hoofdletter en zonder punt, zodat de aanroeper hem in een zin of na een
    /// datumkop kan plaatsen. Leeg bij een leeg dagdeel.
    /// </summary>
    public static string GeenRuimteDeelzin(string? dagdeel)
    {
        var omschrijving = Omschrijving(dagdeel);
        return omschrijving == null ? "" : $"in {omschrijving} is helaas niets beschikbaar";
    }

    /// <summary>
    /// Sluit een antwoord waarvan de vensters of tijden binnen één dagdeel zijn gezocht af met
    /// <see cref="ControleZin"/>, zodat duidelijk is dat andere dagdelen niet zijn meegenomen. Zonder dagdeel
    /// blijft de tekst ongewijzigd.
    /// </summary>
    public static string VoegControleZinToe(string inhoud, string? dagdeel)
        => Normaliseer(dagdeel) == null ? inhoud : inhoud + "\n\n" + ControleZin(dagdeel);

    /// <summary>
    /// <see cref="ControleZin"/> als eigen alinea (met witregel erna), voor een antwoord waarin meerdere datums
    /// samen één gecontroleerd dagdeel delen; leeg als geen van de datums een dagdeel heeft.
    /// </summary>
    public static string ControleAlinea(IEnumerable<string?> dagdelen)
    {
        var dagdeel = dagdelen.FirstOrDefault(d => Normaliseer(d) != null);
        return dagdeel == null ? "" : ControleZin(dagdeel) + "\n\n";
    }

    /// <summary>
    /// De "geen veld beschikbaar"-zin van een antwoord. Is er binnen één dagdeel gezocht, dan staat dat
    /// expliciet in de zin en volgen bewust geen alternatieven uit andere dagdelen; de plannerreden blijft dan
    /// weg omdat die over de hele dag spreekt.
    /// </summary>
    public static string GeenVeldZin(string datumTekst, string? dagdeel, string? reden)
    {
        if (Normaliseer(dagdeel) != null)
            return $"Op {datumTekst} {GeenRuimteDeelzin(dagdeel)}.";

        var zin = $"Op {datumTekst} is helaas geen veld beschikbaar.";
        return string.IsNullOrEmpty(reden) ? zin : $"{zin} {reden}";
    }

    /// <summary>
    /// De "geen veld beschikbaar"-regel van één datum in een antwoord met meerdere datums (met datumkop en
    /// regeleinde). Binnen één dagdeel gezocht: dat staat er expliciet, zonder de dagbrede plannerreden.
    /// </summary>
    public static string GeenVeldRegel(string datumTekst, string? dagdeel, string? reden)
    {
        if (Normaliseer(dagdeel) != null)
            return $"**{datumTekst}:** {GeenRuimteZin(dagdeel)}\n";

        return $"**{datumTekst}:** Helaas geen veld beschikbaar." + (string.IsNullOrEmpty(reden) ? "" : $" {reden}") + "\n";
    }

    /// <summary>
    /// Zoals <see cref="GeenRuimteDeelzin"/>, als losse zin met hoofdletter en punt (voor onder een datumkop).
    /// </summary>
    public static string GeenRuimteZin(string? dagdeel)
    {
        var deel = GeenRuimteDeelzin(dagdeel);
        return deel.Length == 0 ? "" : char.ToUpperInvariant(deel[0]) + deel[1..] + ".";
    }

    private static string Tijd(TimeOnly tijd) => tijd.ToString("HH:mm", CultureInfo.InvariantCulture);
}
