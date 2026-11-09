namespace Planner.Shared.Email;

/// <summary>
/// Bouwt onderwerp en tekst van de automatische antwoorden per classificatietype, met of zonder sjabloon uit de
/// database (#1758, slice 6 van epic #1716). Stond woordelijk per tier als <c>BerichtResponseGenerator</c>; die
/// klassen zijn nu een dun omhulsel dat de tier-DTO's en de instellingenbron doorgeeft.
/// <para>
/// Zuivere logica: geen database, Graph of configuratie. Wat per tier verschilt komt via de poorten in
/// <c>BerichtAntwoordPoorten.cs</c>: de plannergegevens (<see cref="IAntwoordBeschikbaarheid"/>,
/// <see cref="IAntwoordWedstrijd"/>, <see cref="IAntwoordHerplanOpties"/>), het bericht
/// (<see cref="IAntwoordAfzender"/>), een al opgehaald sjabloon (<see cref="IAntwoordSjabloon"/>) en de
/// clubinstellingen (<see cref="IAntwoordInstellingen"/>). De enige omgevingsafhankelijkheden zijn de klok (aanhef)
/// en de procesbrede reviewmodus (<see cref="EmailReviewModus"/>), net als voorheen.
/// </para>
/// </summary>
public static partial class BerichtAntwoordGenerator
{
    // ── Beschikbaarheid ──

    public static (string onderwerp, string body) BouwBeschikbaarheidAntwoord(
        IAntwoordBeschikbaarheid response,
        BerichtClassificatie classificatie,
        IAntwoordAfzender email,
        IAntwoordInstellingen instellingen)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);
        var vensters = response.BeschikbareVensters != null
            ? FilterKunstgrasVensters(response.BeschikbareVensters)
            : new List<IAntwoordVenster>();
        var isDiverseMogelijkheden = vensters.Count >= 4;
        var datumTekst = FormatDatum(classificatie.Datum);

        string inhoud;

        if (response.Beschikbaar && response.Toewijzing != null)
        {
            var t = response.Toewijzing;
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Op {datumTekst} is {t.VeldNaam} beschikbaar om {t.AanvangsTijd}. "
                   + $"De wedstrijd eindigt om {t.EindTijd}.";

            if (isDiverseMogelijkheden)
                inhoud += " Er zijn op deze dag nog diverse andere mogelijkheden.";

            if (response.Waarschuwingen.Count > 0)
                inhoud += "\n\nLet op: " + string.Join(" ", response.Waarschuwingen);
        }
        else if (!response.Beschikbaar && vensters.Count > 0 && !string.IsNullOrEmpty(classificatie.AanvangsTijd))
        {
            // Voorkeurstijd niet beschikbaar — toon vensters als alternatieven
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Op {datumTekst} om {classificatie.AanvangsTijd} is helaas geen ruimte. Beschikbare mogelijkheden:\n";
            foreach (var v in vensters)
                inhoud += FormatVensterRegel(v);
            inhoud += "\nGeef een voorkeurstijd door, dan plannen we het in.";
        }
        else if (response.BeschikbareVensters?.Count > 0)
        {
            // Open vraag zonder voorkeurstijd — toon beschikbare vensters
            vensters = FilterKunstgrasVensters(response.BeschikbareVensters);
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Op {datumTekst} zijn de volgende mogelijkheden:\n";
            foreach (var v in vensters)
                inhoud += FormatVensterRegel(v);
            inhoud += "\nGeef een voorkeurstijd door, dan plannen we het in.";
        }
        else if (!response.Beschikbaar && response.Alternatieven.Count > 0)
        {
            var alternatieven = FilterAlternatieven(response.Alternatieven);
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"Op {datumTekst}";
            if (!string.IsNullOrEmpty(classificatie.AanvangsTijd))
                inhoud += $" om {classificatie.AanvangsTijd}";
            inhoud += " is helaas geen ruimte.";

            if (alternatieven.Count > 0)
            {
                inhoud += " Alternatieven:\n";
                inhoud += FormatAlternatievenLijst(alternatieven);
            }

            if (response.Waarschuwingen.Count > 0)
                inhoud += "\nLet op: " + string.Join(" ", response.Waarschuwingen);
        }
        else if (response.HeeftTeamConflict)
        {
            // Team heeft al een wedstrijd op deze datum — dit is de primaire reden.
            // Veld-beschikbaarheid is niet gecheckt (early return in de planner),
            // dus "geen veld beschikbaar" is hier feitelijk onjuist en misleidend.
            inhoud = $"{aanhef} {voornaam},\n\n"
                   + $"{response.Reden}\n\n"
                   + $"Hierdoor kan op {datumTekst} geen oefenwedstrijd worden ingepland.";
        }
        else
        {
            inhoud = $"{aanhef} {voornaam},\n\n" + DagdeelVenster.GeenVeldZin(datumTekst, response.GecontroleerdDagdeel, response.Reden);
        }

        inhoud = DagdeelVenster.VoegControleZinToe(inhoud, response.GecontroleerdDagdeel);

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, instellingen);
    }

    // ── Beschikbaarheid (meerdere datums) ──

    public static (string onderwerp, string body) BouwMultiDatumBeschikbaarheidAntwoord(
        IReadOnlyList<(string datum, IAntwoordBeschikbaarheid response)> resultaten,
        BerichtClassificatie classificatie,
        IAntwoordAfzender email,
        IAntwoordInstellingen instellingen)
    {
        var aanhef = GetTijdsgebondenAanhef();
        var voornaam = ExtractVoornaam(email.AfzenderNaam);

        var inhoud = $"{aanhef} {voornaam},\n\n";

        foreach (var (datum, response) in resultaten)
        {
            inhoud += BouwDatumSectie(datum, response, classificatie) + "\n";
        }

        inhoud += DagdeelVenster.ControleAlinea(resultaten.Select(r => r.response.GecontroleerdDagdeel));

        inhoud += BouwMultiDatumAfsluitzin(instellingen);

        return WrapMetReviewEnHandtekening(inhoud, classificatie, email, instellingen);
    }

    /// <summary>
    /// Afsluitzin van een multi-datum beschikbaarheidsantwoord, met de coördinator bij naam (#670).
    ///
    /// <para>
    /// Leest de instelling via dezelfde bron als <see cref="GetHandtekening"/>: de tier levert de meegegeven
    /// club-snapshot wanneer die er is, en anders de globale cache. Dat is de regel uit #677 — een dry-run voor de
    /// democlub mag niet terugvallen op de gegevens van de productieclub.
    /// </para>
    ///
    /// <para>
    /// Ontbreekt de naam, dan blijft de zin clubneutraal en compleet: geen halve zin, en bewust geen
    /// fallbacknaam in de code (architectuurregel "geen club-specifieke strings").
    /// </para>
    /// </summary>
    private static string BouwMultiDatumAfsluitzin(IAntwoordInstellingen instellingen)
    {
        var coordinatorNaam = instellingen.CoordinatorNaam;

        return string.IsNullOrWhiteSpace(coordinatorNaam)
            ? "Laat weten welke optie(s) de voorkeur hebben, dan gaan we samen plannen en definitief opnemen in de planning."
            : $"Laat weten welke optie(s) de voorkeur hebben, dan gaan we samen met {coordinatorNaam} plannen en definitief opnemen in de planning.";
    }

    /// <summary>
    /// Bouwt de sectie voor één datum in een (multi-datum) beschikbaarheidantwoord.
    /// Toont vensters (van-tot) i.p.v. vaste starttijden, en "diverse mogelijkheden" bij een ruime planning.
    /// </summary>
    private static string BouwDatumSectie(
        string datum, IAntwoordBeschikbaarheid response, BerichtClassificatie classificatie)
    {
        var datumTekst = FormatDatum(datum);
        var vensters = response.BeschikbareVensters != null
            ? FilterKunstgrasVensters(response.BeschikbareVensters)
            : new List<IAntwoordVenster>();
        var isDiverseMogelijkheden = vensters.Count >= 4;

        if (response.Beschikbaar && response.Toewijzing != null)
        {
            var t = response.Toewijzing;
            var sectie = $"**{datumTekst}:** {t.VeldNaam} is beschikbaar om {t.AanvangsTijd} (eindigt {t.EindTijd}).";
            if (response.Waarschuwingen.Count > 0)
                sectie += " Let op: " + string.Join(" ", response.Waarschuwingen);
            if (isDiverseMogelijkheden)
                sectie += " Er zijn op deze dag nog diverse andere mogelijkheden.";
            return sectie + "\n";
        }

        if (!response.Beschikbaar && vensters.Count > 0)
        {
            // Voorkeurstijd niet beschikbaar — toon vensters als alternatieven
            var sectie = $"**{datumTekst}:**";
            if (!string.IsNullOrEmpty(classificatie.AanvangsTijd))
                sectie += $" Om {classificatie.AanvangsTijd}";
            sectie += " is helaas geen ruimte. Beschikbare mogelijkheden:\n";
            foreach (var v in vensters)
                sectie += FormatVensterRegel(v);
            return sectie;
        }

        if (!response.Beschikbaar && response.Alternatieven.Count > 0)
        {
            var alternatieven = FilterAlternatieven(response.Alternatieven);
            var sectie = $"**{datumTekst}:**";
            if (!string.IsNullOrEmpty(classificatie.AanvangsTijd))
                sectie += $" Om {classificatie.AanvangsTijd}";
            sectie += " is helaas geen ruimte.";
            if (alternatieven.Count > 0)
            {
                sectie += " Alternatieven:\n";
                sectie += FormatAlternatievenLijst(alternatieven);
            }
            return sectie;
        }

        if (response.HeeftTeamConflict)
            return $"**{datumTekst}:** {response.Reden} Hierdoor kan op deze dag geen oefenwedstrijd worden ingepland.\n";

        return DagdeelVenster.GeenVeldRegel(datumTekst, response.GecontroleerdDagdeel, response.Reden);
    }

    // ── Template-driven antwoord (sjabloon uit de database) ──

    /// <summary>
    /// Past een sjabloon toe op de classificatie. Placeholders: {{voornaam}}, {{aanhef}},
    /// {{datum}}, {{team}}, {{tegenstander}}, {{aanvangstijd}}, {{dagdeel}}, {{coordinator}}; per antwoord
    /// komen daar de <paramref name="extraPlaceholders"/> bij (<c>BerichtAntwoordGenerator.Sjabloon.cs</c>, #1780).
    /// Valt terug op de standaard handtekening + review-wrapper.
    /// Niet-destructief: bestaande Bouw* methoden blijven beschikbaar als fallback.
    /// </summary>
    public static (string onderwerp, string body) BouwAangepasteAntwoord(
        IAntwoordSjabloon template,
        BerichtClassificatie classificatie,
        IAntwoordAfzender email,
        IAntwoordInstellingen instellingen,
        IDictionary<string, string>? extraPlaceholders = null,
        string? gecontroleerdDagdeel = null)
    {
        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["voornaam"] = ExtractVoornaam(email.AfzenderNaam),
            ["aanhef"] = GetTijdsgebondenAanhef(),
            ["datum"] = FormatDatum(classificatie.Datum),
            ["team"] = AiVeldSanering.TeamNaamOfGeneriek(classificatie.TeamNaam, classificatie.TeamNaamOnbevestigd),
            ["tegenstander"] = AiVeldSanering.TegenstanderOfGeneriek(classificatie.Tegenstander),
            ["aanvangstijd"] = classificatie.AanvangsTijd ?? "",
            // #1587: "de ochtend (08:30 - 12:00)" of leeg. Uitsluitend het dagdeel dat de planner werkelijk heeft
            // gecontroleerd, nooit het gevraagde dagdeel uit de classificatie (dat kan vervallen zijn).
            ["dagdeel"] = DagdeelVenster.Omschrijving(gecontroleerdDagdeel) ?? "",
            ["coordinator"] = instellingen.CoordinatorNaam ?? "",
        };

        if (extraPlaceholders != null)
        {
            foreach (var (k, v) in extraPlaceholders)
                placeholders[k] = v;
        }

        var onderwerpInvulling = VulPlaceholdersIn(template.Onderwerp, placeholders);
        var bodyInvulling = VulPlaceholdersIn(template.Body, placeholders);
        // Noemt het sjabloon het dagdeel zelf niet, dan komt de standaardzin alsnog onder de sjabloontekst.
        bodyInvulling = DagdeelVenster.VoegControleZinToeTenzijVermeld(bodyInvulling, gecontroleerdDagdeel, template.Body);

        var onderwerp = !string.IsNullOrWhiteSpace(onderwerpInvulling)
            ? onderwerpInvulling
            : $"Re: {email.Onderwerp}";

        // Wrap met review-mode prefix + handtekening, hergebruik de bestaande logica
        var body = "";
        if (EmailReviewModus.IsActief())
        {
            body += $"=== REVIEW MODE ===\n"
                  + $"Originele afzender: {email.Afzender}\n"
                  + $"Onderwerp: {email.Onderwerp}\n"
                  + $"Classificatie: {classificatie.Type}\n"
                  + $"Template: {template.Key}\n"
                  + $"==================\n\n";
        }

        // TrimEnd (#1780): een placeholder aan het eind van de tekst die leeg blijft (bijvoorbeeld {{bijlage_zin}})
        // mag geen rij lege regels voor de handtekening achterlaten; de handtekening voegt zelf een lege regel toe.
        body += bodyInvulling.TrimEnd();
        body += "\n\n" + GetHandtekening(instellingen);
        return (onderwerp, body);
    }

    /// <summary>
    /// Past een sjabloon toe met simpele placeholder-substitutie (<c>{{key}}</c>, hoofdletterongevoelig); een
    /// onbekende placeholder blijft staan. Ook gebruikt door <c>EmailTemplateService.ApplyPlaceholders</c> per tier.
    /// </summary>
    public static string VulPlaceholdersIn(string body, IDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(body) || values == null || values.Count == 0) return body;
        foreach (var (key, value) in values)
        {
            body = body.Replace("{{" + key + "}}", value ?? "", StringComparison.OrdinalIgnoreCase);
        }
        return body;
    }

    // ── Helpers ──

    private static string FormatVensterRegel(IAntwoordVenster v)
    {
        var totTekst = IsEindeDag(v.Tot) ? "einde dag" : v.Tot;
        var regel = $"- {v.VeldNaam}: beschikbaar van {v.Van} tot {totTekst}";
        if (!string.IsNullOrEmpty(v.Opmerking))
            regel += $" ({v.Opmerking})";
        return regel + "\n";
    }

    private static string FormatAlternatievenLijst(IEnumerable<SlotToewijzing> alternatieven)
        => string.Concat(alternatieven.Take(3).Select(alt => $"- {alt.VeldNaam} om {alt.AanvangsTijd} (eindigt {alt.EindTijd})\n"));

    public static string GetTijdsgebondenAanhef() => AntwoordTekst.Aanhef(DateTime.UtcNow);

    /// <summary>
    /// Beperkt de alternatieven tot kunstgras zodra er drie of meer kunstgrasvelden beschikbaar zijn:
    /// op zo'n ruime dag hoeft het antwoord geen natuurgras aan te bieden.
    ///
    /// <para>Het veldtype komt uit de veldentabel (#705). Eerder werd het uit het veldnummer geraden
    /// (1-4 = kunstgras, 5 = gras); bij een club met natuurgras op de lage nummers gooide dat juist
    /// de échte kunstgrasvelden weg en verzweeg het antwoord beschikbare velden.</para>
    ///
    /// <para><b>Alleen aantoonbaar natuurgras verdwijnt (#707).</b> De classificatie loopt via
    /// <see cref="VeldTypeClassificatie"/> — de enige kunstgras-definitie in de codebase. Een veldtype
    /// dat noch als kunstgras noch als natuurgras herkenbaar is (leeg, of bijv. "hybride") geldt als
    /// onbekend: het telt niet mee voor de drempel én wordt nooit weggefilterd. Zo'n slot is
    /// aantoonbaar beschikbaar, en dat weglaten is schadelijker dan een optie te veel aanbieden.</para>
    /// </summary>
    private static List<SlotToewijzing> FilterAlternatieven(List<SlotToewijzing> alternatieven)
        => AntwoordTekst.FilterAlternatieven(alternatieven);

    /// <summary>
    /// Laat natuurgrasvensters weg zodra er drie of meer kunstgrasvensters zijn, behalve als een
    /// natuurgrasvenster het enige aanbod in zijn tijdsblok is — dan is het juist de toegevoegde
    /// waarde. Veldtype uit de veldentabel, niet uit het veldnummer (#705), geclassificeerd via
    /// <see cref="VeldTypeClassificatie"/> (#707); vensters met een onbekend veldtype blijven altijd staan.
    /// </summary>
    private static IReadOnlyList<IAntwoordVenster> FilterKunstgrasVensters(IReadOnlyList<IAntwoordVenster> vensters)
    {
        var kunstgrasVensters = vensters.Where(v => VeldTypeClassificatie.IsKunstgras(v.VeldType)).ToList();
        if (kunstgrasVensters.Count < 3)
            return vensters;

        var overigeVensters = vensters
            .Where(v => !VeldTypeClassificatie.IsKunstgras(v.VeldType))
            .Where(v => !VeldTypeClassificatie.IsNatuurgras(v.VeldType) || !OverlaptMetVenster(v, kunstgrasVensters))
            .ToList();

        return kunstgrasVensters.Concat(overigeVensters)
            .OrderBy(v => TimeOnly.TryParse(v.Van, out var t) ? t : TimeOnly.MinValue)
            .ToList();
    }

    private static bool OverlaptMetVenster(IAntwoordVenster venster, List<IAntwoordVenster> andere)
    {
        var van = TimeOnly.TryParse(venster.Van, out var v) ? v : TimeOnly.MinValue;
        var tot = TimeOnly.TryParse(venster.Tot, out var t) ? t : TimeOnly.MaxValue;
        return andere.Any(a =>
        {
            var aVan = TimeOnly.TryParse(a.Van, out var av) ? av : TimeOnly.MinValue;
            var aTot = TimeOnly.TryParse(a.Tot, out var at) ? at : TimeOnly.MaxValue;
            return aVan < tot && aTot > van;
        });
    }

    /// <summary>
    /// Eindtijd >= 21:00 betekent effectief "einde dag" (sluittijd sportpark).
    /// </summary>
    private static bool IsEindeDag(string tot) => TimeOnly.TryParse(tot, out var tijd) && tijd >= new TimeOnly(21, 0);

    private static string FormatDatum(string? datumString) => AntwoordTekst.Datum(datumString);

    private static (string onderwerp, string body) WrapMetReviewEnHandtekening(
        string inhoud, BerichtClassificatie classificatie, IAntwoordAfzender email,
        IAntwoordInstellingen instellingen)
    {
        var onderwerp = $"Re: {email.Onderwerp}";
        var body = "";

        if (EmailReviewModus.IsActief())
        {
            body += $"=== REVIEW MODE ===\n"
                  + $"Originele afzender: {email.Afzender}\n"
                  + $"Onderwerp: {email.Onderwerp}\n"
                  + $"Classificatie: {classificatie.Type}\n"
                  + $"==================\n\n";
        }

        body += inhoud;
        body += "\n\n" + GetHandtekening(instellingen);

        return (onderwerp, body);
    }

    /// <summary>
    /// Bouwt de auto-reply handtekening uit <paramref name="instellingen"/>. De tier levert daarvoor de
    /// club-snapshot van het dry-run pad (EmailTestFunction, #677) of, zonder snapshot, de procesbrede cache.
    /// Een ontbrekend veld valt binnen een snapshot NIET terug op de globale cache — dat zou de echte
    /// productieclub laten lekken in een democlub-dry-run; dat onderscheid zit in de tier-instellingenbron.
    /// </summary>
    private static string GetHandtekening(IAntwoordInstellingen instellingen)
    {
        var voetnoot = instellingen.EmailVoetnoot;
        if (!string.IsNullOrWhiteSpace(voetnoot))
            return voetnoot;

        // Fallback: auto-opgebouwde handtekening uit losse instellingen
        var afzenderNaam = instellingen.PlannerAfzenderNaam
            ?? throw new InvalidOperationException(instellingen.OntbrekendeAfzenderNaamMelding);
        var coordinatorNaam = instellingen.CoordinatorNaam;
        var coordinatorFunctie = instellingen.CoordinatorFunctie;

        var handtekening = $"Met vriendelijke groet,\n\n{afzenderNaam}";
        if (!string.IsNullOrEmpty(coordinatorNaam))
            handtekening += $"\nGeautomatiseerd antwoord namens {coordinatorNaam}";
        if (!string.IsNullOrEmpty(coordinatorFunctie))
            handtekening += $"\n{coordinatorFunctie}";

        return handtekening;
    }

    private static string ExtractVoornaam(string volledigeNaam) => AntwoordTekst.Voornaam(volledigeNaam);
}
