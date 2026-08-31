namespace Planner.Shared;

/// <summary>
/// AVG-maskering van persoonsgegevens in antwoorden die naar de browser gaan (#858).
///
/// <para>
/// <b>Waarom dit bestaat.</b> Beide tiers bouwden hun antwoordrij als
/// <c>Dictionary&lt;string, object?&gt;</c> met <c>reader.GetName(i)</c> als sleutel, en maskeerden
/// daarna met een letterlijke, hoofdlettergevoelige opzoeking op <c>"Afzender"</c>. Die constructie
/// heeft twee stille faalwijzen die allebei hetzelfde opleveren — een volledig e-mailadres in de
/// browser, zonder enige melding:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Casing.</b> De lowercase-conventie van een niet-SQL-Server-tier
///     (ARCHITECTUUR-DATABASE-TIERS.md §3) levert <c>afzender</c> op. Een ordinale
///     <c>TryGetValue("Afzender")</c> mist die sleutel en slaat de maskering over.
///   </description></item>
///   <item><description>
///     <b>Een verdwenen kolom of alias.</b> Wie de SELECT aanpast en het alias laat vallen, krijgt
///     geen foutmelding: <c>TryGetValue</c> geeft simpelweg <c>false</c> terug en de rij gaat
///     ongemaskeerd door.
///   </description></item>
/// </list>
///
/// <para>
/// <b>De oplossing is bewust luidruchtig.</b> <see cref="MaskeerAfzender"/> zoekt
/// hoofdletterongevoelig én gooit een <see cref="InvalidOperationException"/> als er niets te
/// maskeren viel. Een maskeerstap die niets vond is een fout in de query, geen no-op — precies
/// dezelfde redenering als "overslaan is falen" in de zelftest (#851). Liever een zichtbare 500 dan
/// een stille AVG-schending.
/// </para>
/// </summary>
public static class AvgMaskering
{
    /// <summary>
    /// Vervangt het afzenderadres in <paramref name="rij"/> door <c>***@domein</c> — of door
    /// <c>***</c> als er geen apenstaartje in staat.
    /// </summary>
    /// <param name="rij">De antwoordrij; wordt ter plekke aangepast.</param>
    /// <param name="kolomNaam">
    /// De logische kolomnaam, hoofdletterongevoelig vergeleken. Default <c>Afzender</c>.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Als <paramref name="rij"/> geen kolom met die naam bevat. Zie de klasse-doc-comment: dat
    /// betekent dat de SELECT de kolom niet (meer) oplevert, en dan hoort de aanroep te knallen in
    /// plaats van stilzwijgend niets te maskeren.
    /// </exception>
    public static void MaskeerAfzender(Dictionary<string, object?> rij, string kolomNaam = "Afzender")
    {
        ArgumentNullException.ThrowIfNull(rij);

        var sleutel = VindSleutel(rij, kolomNaam)
            ?? throw new InvalidOperationException(
                $"AVG-maskering (#858): kolom '{kolomNaam}' ontbreekt in de antwoordrij, dus er is " +
                "niets gemaskeerd. Dit is een fout in de query — controleer of de SELECT de kolom " +
                "nog oplevert. Stilzwijgend doorgaan zou een onvermaskerd adres naar de browser sturen.");

        rij[sleutel] = Maskeer(rij[sleutel] as string);
    }

    /// <summary>
    /// Het maskeerpatroon zelf: alleen het domein blijft over, nooit het volledige adres.
    /// <c>null</c> en lege waarden blijven <c>null</c> — er valt dan niets te lekken.
    /// </summary>
    public static string? Maskeer(string? emailadres)
    {
        if (string.IsNullOrWhiteSpace(emailadres)) return emailadres;
        var at = emailadres.IndexOf('@');
        return at > 0 ? "***" + emailadres[at..] : "***";
    }

    private static string? VindSleutel(Dictionary<string, object?> rij, string kolomNaam)
    {
        if (rij.ContainsKey(kolomNaam)) return kolomNaam;
        foreach (var key in rij.Keys)
            if (string.Equals(key, kolomNaam, StringComparison.OrdinalIgnoreCase))
                return key;
        return null;
    }
}
