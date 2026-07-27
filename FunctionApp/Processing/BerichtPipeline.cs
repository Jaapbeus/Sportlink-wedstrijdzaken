using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SportlinkFunction.Email;
using SportlinkFunction.Planner;
using SportlinkFunction.TeamResolution;

namespace SportlinkFunction.Processing;

/// <summary>
/// Kanaal-agnostische verwerkingspipeline voor inkomende berichten.
/// Bevat classificatie-validatie, plannerlogica en antwoordgeneratie,
/// onafhankelijk van het kanaal (email, dry-run, WhatsApp, etc.).
/// </summary>
public static class BerichtPipeline
{
    /// <summary>
    /// Extraheert datums uit onderwerp en body, en corrigeert de AI-classificatie.
    /// Prioriteit: expliciete datum in onderwerp > expliciete datum in body > AI datum + dag-validatie.
    /// In een reply-thread wint de AI-datum boven de (oude) datum in het onderwerp.
    /// </summary>
    public static void ValideerDagDatum(BerichtClassificatie classificatie, string emailBody, string onderwerp)
    {
        // De citaat-/ondertekeningsstaart bevat datums en dagnamen van eerdere berichten
        // ("Verzonden: dinsdag 26 mei 2026"); die horen niet bij dit verzoek.
        var eigenTekst = StripCitaatEnOndertekening(emailBody);

        var onderwerpDatum = ExtractExpliciteDatum(onderwerp);
        // Bij een reply/forward staat in het onderwerp de datum van de oorspronkelijke vraag.
        // Heeft de AI uit de nieuwe tekst een datum gehaald, dan is die actueler.
        var onderwerpMagWinnen = string.IsNullOrEmpty(classificatie.Datum) || !HeeftReplyPrefix(onderwerp);
        if (onderwerpDatum.HasValue && onderwerpMagWinnen)
        {
            classificatie.Datum = onderwerpDatum.Value.ToString("yyyy-MM-dd");
            return;
        }

        var bodyDatum = ExtractExpliciteDatum(eigenTekst);
        if (bodyDatum.HasValue && string.IsNullOrEmpty(classificatie.Datum))
        {
            classificatie.Datum = bodyDatum.Value.ToString("yyyy-MM-dd");
            return;
        }

        if (string.IsNullOrEmpty(classificatie.Datum)) return;
        if (!DateOnly.TryParse(classificatie.Datum, out var datum)) return;

        var tekst = (onderwerp + " " + eigenTekst).ToLowerInvariant();
        var dagNamen = new (string naam, DayOfWeek dag)[]
        {
            ("maandag", DayOfWeek.Monday), ("dinsdag", DayOfWeek.Tuesday),
            ("woensdag", DayOfWeek.Wednesday), ("donderdag", DayOfWeek.Thursday),
            ("vrijdag", DayOfWeek.Friday), ("zaterdag", DayOfWeek.Saturday),
            ("zondag", DayOfWeek.Sunday)
        };

        // Alleen corrigeren bij precies één dagnaam. Bij meerdere ("zaterdag of zondag?", of een
        // ondertekening met "kantine open vrijdag") is niet vast te stellen welke dag bij het
        // verzoek hoort; dan is de AI-datum ongewijzigd laten beter dan gokken.
        var gevondenDagen = dagNamen
            .Where(d => tekst.Contains(d.naam))
            .Select(d => d.dag)
            .Distinct()
            .ToList();
        if (gevondenDagen.Count != 1) return;

        var doelDag = gevondenDagen[0];
        if (datum.DayOfWeek == doelDag) return;

        for (int offset = 1; offset <= 7; offset++)
        {
            if (datum.AddDays(-offset).DayOfWeek == doelDag)
                { classificatie.Datum = datum.AddDays(-offset).ToString("yyyy-MM-dd"); return; }
            if (datum.AddDays(offset).DayOfWeek == doelDag)
                { classificatie.Datum = datum.AddDays(offset).ToString("yyyy-MM-dd"); return; }
        }
    }

    /// <summary>
    /// Vertaalt de AI-classificatie naar de juiste PlannerService-aanroep.
    /// </summary>
    /// <param name="clubCode">
    /// Expliciete club-override (#677) — gebruikt door het dry-run pad (EmailTestFunction) om de
    /// GUI-clubswitcher (bijv. AllStars FC) te respecteren. Blijft <c>null</c> voor de echte
    /// e-mailpipeline (EmailProcessorFunction, geen clubswitcher), die daardoor exact als voorheen
    /// op de primaire club van deze deployment blijft resolven.
    /// </param>
    /// <param name="teamResolutie">
    /// Optionele teamnaam→TeamId-vertaallaag (#698/#699). Alleen actief als
    /// <c>TeamResolverMode</c> op <c>shadow</c> of <c>on</c> staat; blijft de parameter
    /// <c>null</c> (of de stand <c>off</c>), dan gedraagt de pipeline zich exact als voorheen.
    /// </param>
    public static async Task<string> VerwerkMetPlannerAsync(
        BerichtClassificatie classificatie, InkomendBericht bericht, ILogger log,
        string? clubCode = null, ClubAppSettingsSnapshot? clubSettings = null,
        TeamResolutieContext? teamResolutie = null)
    {
        classificatie.LeeftijdsCategorie = NormaliseerLeeftijdsCategorie(classificatie.LeeftijdsCategorie);

        var team = classificatie.TeamNaam ?? "";
        var tegenstander = classificatie.Tegenstander ?? "";
        var ruweTeamTekst = team;

        if (!string.IsNullOrWhiteSpace(team) && !string.IsNullOrWhiteSpace(tegenstander))
        {
            var cc = ResolveHeuristicClubCode(clubCode);
            bool teamIsEigenClub = !team.Contains(' ')
                || (!string.IsNullOrWhiteSpace(cc) && team.StartsWith(cc, StringComparison.OrdinalIgnoreCase));
            bool tegenstanderIsEigenClub = !tegenstander.Contains(' ')
                || (!string.IsNullOrWhiteSpace(cc) && tegenstander.StartsWith(cc, StringComparison.OrdinalIgnoreCase));

            if (!teamIsEigenClub && tegenstanderIsEigenClub)
            {
                classificatie.TeamNaam = tegenstander;
                classificatie.Tegenstander = team;
                ruweTeamTekst = tegenstander;
            }
        }

        classificatie.TeamNaam = NormaliseerTeamNaam(classificatie.TeamNaam, clubCode);
        classificatie.TeamNaam = await PasTeamResolutieToeAsync(
            teamResolutie, ruweTeamTekst, classificatie.TeamNaam, clubCode, log);

        switch (classificatie.Type)
        {
            case VerzoekType.BeschikbaarheidCheck:
                var alleDatums = ExpandDoordeweeksDatums(
                    classificatie.GetAlleDatums(), bericht.Onderwerp, bericht.Body);

                var cc2 = ResolveHeuristicClubCode(clubCode);
                bool heeftExterneTegenstander = !string.IsNullOrWhiteSpace(classificatie.Tegenstander)
                    && (string.IsNullOrWhiteSpace(cc2)
                        || !classificatie.Tegenstander.StartsWith(cc2, StringComparison.OrdinalIgnoreCase));
                bool heeftOnbekendVrcTeam = string.IsNullOrWhiteSpace(classificatie.TeamNaam)
                    || string.IsNullOrWhiteSpace(cc2)
                    || !classificatie.TeamNaam.StartsWith(cc2, StringComparison.OrdinalIgnoreCase);

                if (heeftExterneTegenstander && heeftOnbekendVrcTeam && alleDatums.Count == 1
                    && DateOnly.TryParse(alleDatums[0], out var opponentCheckDatum))
                {
                    var wedstrijdOpDatum = await PlannerDataAccess.FindMatchByOpponentAsync(
                        classificatie.Tegenstander!, opponentCheckDatum, clubCode);
                    if (wedstrijdOpDatum != null)
                        return JsonConvert.SerializeObject(new { wedstrijdAlIngepland = true, wedstrijd = wedstrijdOpDatum });

                    var wedstrijdAndereDatum = await PlannerDataAccess.FindMatchByOpponentAsync(
                        classificatie.Tegenstander!, null, clubCode);
                    if (wedstrijdAndereDatum == null)
                        return JsonConvert.SerializeObject(new { teamOnbekend = true, tegenstander = classificatie.Tegenstander });

                    var vrcTeam = ExtractEigenTeamUitWedstrijd(wedstrijdAndereDatum.Wedstrijd, classificatie.Tegenstander!, clubCode);
                    if (vrcTeam != null)
                        classificatie.TeamNaam = NormaliseerTeamNaam(vrcTeam, clubCode);
                }

                if (alleDatums.Count > 1)
                {
                    var multiResults = new List<object>();
                    foreach (var datum in alleDatums)
                    {
                        var req = new CheckAvailabilityRequest
                        {
                            Datum = datum,
                            AanvangsTijd = classificatie.AanvangsTijd,
                            LeeftijdsCategorie = classificatie.LeeftijdsCategorie,
                            TeamNaam = classificatie.TeamNaam,
                            Tegenstander = classificatie.Tegenstander,
                            HeelVeld = classificatie.HeelVeld
                        };
                        var resp = await PlannerService.CheckAvailabilityAsync(req, log, clubCode);
                        multiResults.Add(new { datum, response = resp });
                    }
                    return JsonConvert.SerializeObject(new { multiDatum = true, resultaten = multiResults });
                }
                // Dezelfde datum als de tegenstander-lookup hierboven gebruikt: anders loopt de
                // plannercheck over een andere datum dan de rest van de verwerking.
                var primaireDatum = KiesPrimaireDatum(alleDatums, classificatie.Datum);
                if (string.IsNullOrWhiteSpace(primaireDatum) || !DateOnly.TryParse(primaireDatum, out _))
                {
                    // Zonder bruikbare datum heeft een plannercheck geen zin: die levert een
                    // interne foutstring ("Ongeldige datum: .") op die in het antwoord aan de
                    // afzender zou belanden.
                    return JsonConvert.SerializeObject(new { datumOnbekend = true });
                }
                classificatie.Datum = primaireDatum;

                var checkRequest = new CheckAvailabilityRequest
                {
                    Datum = primaireDatum,
                    AanvangsTijd = classificatie.AanvangsTijd,
                    LeeftijdsCategorie = classificatie.LeeftijdsCategorie,
                    TeamNaam = classificatie.TeamNaam,
                    Tegenstander = classificatie.Tegenstander,
                    HeelVeld = classificatie.HeelVeld
                };
                var checkResponse = await PlannerService.CheckAvailabilityAsync(checkRequest, log, clubCode);
                return JsonConvert.SerializeObject(checkResponse);

            case VerzoekType.HerplanVerzoek:
                if (!string.IsNullOrEmpty(classificatie.TeamNaam) && !string.IsNullOrEmpty(classificatie.Datum))
                {
                    if (DateOnly.TryParse(classificatie.Datum, out var datum))
                    {
                        var wedstrijd = await PlannerDataAccess.FindMatchAsync(classificatie.TeamNaam, datum, clubCode);
                        if (wedstrijd != null)
                        {
                            var deadlineDagen = clubSettings?.HerplanDeadlineDagen
                                ?? (int.TryParse(SystemUtilities.AppSettings.GetSetting("herplanDeadlineDagen"), out var dd) ? dd : 8);
                            if (DateOnly.TryParse(wedstrijd.Datum, out var wedstrijdDatum))
                            {
                                var dagenTotWedstrijd = (wedstrijdDatum.ToDateTime(TimeOnly.MinValue) - DateTime.Today).TotalDays;
                                if (dagenTotWedstrijd < deadlineDagen)
                                {
                                    return JsonConvert.SerializeObject(new
                                    {
                                        herplanTeLaat = true,
                                        wedstrijd,
                                        deadlineDagen,
                                        dagenTotWedstrijd = (int)dagenTotWedstrijd
                                    });
                                }
                            }

                            if (!string.IsNullOrEmpty(classificatie.GewensteDatum))
                            {
                                var gewenstRequest = new CheckAvailabilityRequest
                                {
                                    Datum = classificatie.GewensteDatum,
                                    LeeftijdsCategorie = classificatie.LeeftijdsCategorie,
                                    TeamNaam = classificatie.TeamNaam
                                };
                                var beschikbaarheid = await PlannerService.CheckAvailabilityAsync(gewenstRequest, log, clubCode);
                                return JsonConvert.SerializeObject(new { wedstrijd, gewensteDatum = classificatie.GewensteDatum, beschikbaarheid });
                            }

                            var herplanRequest = new HerplanCheckRequest
                            {
                                Wedstrijdcode = wedstrijd.Wedstrijdcode,
                                VoorkeurTijd = classificatie.AanvangsTijd,
                                Richting = DetecteerRichting(bericht.Onderwerp, bericht.Body)
                            };
                            var herplanResponse = await PlannerService.CheckRescheduleAvailabilityAsync(herplanRequest, log, clubCode);
                            return JsonConvert.SerializeObject(new { wedstrijd, herplanOpties = herplanResponse });
                        }
                        return JsonConvert.SerializeObject(new { gevonden = false, reden = $"Geen wedstrijd gevonden voor {classificatie.TeamNaam} op {classificatie.Datum}" });
                    }
                }
                return JsonConvert.SerializeObject(new { error = "Onvoldoende gegevens voor herplanverzoek (team en datum nodig)" });

            case VerzoekType.TeamContactOpvragen:
                if (!string.IsNullOrWhiteSpace(classificatie.TeamNaam))
                {
                    // clubCode meegeven zodat een dry-run met de demoklub geselecteerd niet de
                    // begeleidingscontacten van de productieclub raadpleegt (#677/#706).
                    var contact = await PlannerDataAccess.GetTeamleiderContactAsync(classificatie.TeamNaam, clubCode);
                    return JsonConvert.SerializeObject(new
                    {
                        teamContactOpgevraagd = true,
                        teamNaam = classificatie.TeamNaam,
                        coachGevonden = contact != null
                    });
                }
                return JsonConvert.SerializeObject(new { teamContactOpgevraagd = true, teamNaam = (string?)null, coachGevonden = false });

            case VerzoekType.Bevestiging:
                return JsonConvert.SerializeObject(new { status = "Bevestiging ontvangen", opmerking = "Bevestigingen vereisen handmatige afhandeling door de coördinator" });

            default:
                return JsonConvert.SerializeObject(new { status = "Niet verwerkt" });
        }
    }

    /// <summary>
    /// Bouwt het antwoord via templates op basis van het classificatietype en PlannerService response.
    /// Probeert eerst een DB-override op te halen via EmailTemplateService (#287).
    /// Valt terug op hardcoded defaults als er geen override is.
    /// </summary>
    /// <param name="clubSettings">
    /// Club-specifieke AppSettings-snapshot (#677) — gebruikt door het dry-run pad zodat de
    /// auto-reply handtekening (afzendernaam, coördinator, voetnoot) van de in de GUI geselecteerde
    /// club komt in plaats van de proces-globale cache. <c>null</c> voor de echte e-mailpipeline:
    /// die blijft de globale cache gebruiken, exact als voorheen.
    /// </param>
    public static async Task<(string onderwerp, string body)> BouwTemplateAntwoord(
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        InkomendBericht bericht,
        ILogger? log = null,
        ClubAppSettingsSnapshot? clubSettings = null)
    {
        switch (classificatie.Type)
        {
            case VerzoekType.BeschikbaarheidCheck:
                var jobj = Newtonsoft.Json.Linq.JObject.Parse(plannerResponseJson);

                if (jobj["wedstrijdAlIngepland"]?.ToObject<bool>() == true)
                {
                    var ingeplandWedstrijd = jobj["wedstrijd"]?.ToObject<ZoekWedstrijdResponse>();
                    return BerichtResponseGenerator.BouwWedstrijdAlIngeplandAntwoord(
                        ingeplandWedstrijd, classificatie, bericht, clubSettings);
                }

                if (jobj["teamOnbekend"]?.ToObject<bool>() == true)
                {
                    var onbekendeTegenstander = jobj["tegenstander"]?.ToString()
                        ?? classificatie.Tegenstander ?? "";
                    return BerichtResponseGenerator.BouwTeamOnbekendAntwoord(
                        onbekendeTegenstander, classificatie, bericht, clubSettings);
                }

                if (jobj["datumOnbekend"]?.ToObject<bool>() == true)
                {
                    // BerichtResponseGenerator heeft hier geen eigen Bouw*-methode voor; via de
                    // template-route krijgt dit antwoord dezelfde review-prefix en handtekening
                    // als alle andere antwoorden.
                    var datumOnbekendTemplate = new EmailTemplate(
                        "datum_onbekend",
                        "",
                        "{{aanhef}} {{voornaam}},\n\n"
                        + "Bedankt voor je bericht. We konden er geen concrete datum uit opmaken. "
                        + "Kun je aangeven welke datum of datums je in gedachten hebt? "
                        + "Dan controleren we de beschikbaarheid van onze velden voor je.");
                    return BerichtResponseGenerator.BouwAangepasteAntwoord(
                        datumOnbekendTemplate, classificatie, bericht, clubSettings);
                }

                if (jobj["multiDatum"]?.ToObject<bool>() == true)
                {
                    var resultaten = new List<(string datum, CheckAvailabilityResponse response)>();
                    foreach (var item in jobj["resultaten"]!)
                    {
                        var datum = item["datum"]?.ToString() ?? "";
                        var resp = item["response"]?.ToObject<CheckAvailabilityResponse>() ?? new CheckAvailabilityResponse();
                        resultaten.Add((datum, resp));
                    }
                    return BerichtResponseGenerator.BouwMultiDatumBeschikbaarheidAntwoord(
                        resultaten, classificatie, bericht, clubSettings);
                }

                var beschikbaarheidTemplate = await Email.EmailTemplateService.GetTemplateAsync("beschikbaarheid_check", log);
                if (beschikbaarheidTemplate != null)
                    return BerichtResponseGenerator.BouwAangepasteAntwoord(beschikbaarheidTemplate, classificatie, bericht, clubSettings);

                var checkResponse = JsonConvert.DeserializeObject<CheckAvailabilityResponse>(plannerResponseJson);
                return BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
                    checkResponse ?? new CheckAvailabilityResponse(), classificatie, bericht, clubSettings);

            case VerzoekType.HerplanVerzoek:
                var herplanData = Newtonsoft.Json.Linq.JObject.Parse(plannerResponseJson);
                var wedstrijd = herplanData["wedstrijd"]?.ToObject<ZoekWedstrijdResponse>();

                if (herplanData["herplanTeLaat"]?.ToObject<bool>() == true)
                {
                    var teLaatWedstrijd = herplanData["wedstrijd"]?.ToObject<ZoekWedstrijdResponse>();
                    var deadlineDagen = herplanData["deadlineDagen"]?.ToObject<int>() ?? 8;
                    var dagenTot = herplanData["dagenTotWedstrijd"]?.ToObject<int>() ?? 0;
                    return BerichtResponseGenerator.BouwHerplanTeLaatAntwoord(teLaatWedstrijd, deadlineDagen, dagenTot, classificatie, bericht, clubSettings);
                }

                if (herplanData["gewensteDatum"] != null && herplanData["beschikbaarheid"] != null)
                {
                    var gewensteDatum = herplanData["gewensteDatum"]?.ToString();
                    var beschikbaarheid = herplanData["beschikbaarheid"]?.ToObject<CheckAvailabilityResponse>();
                    return BerichtResponseGenerator.BouwHerplanGewensteDatumAntwoord(
                        wedstrijd, gewensteDatum, beschikbaarheid, classificatie, bericht, clubSettings);
                }

                var herplanTemplate = await Email.EmailTemplateService.GetTemplateAsync("herplan_verzoek", log);
                if (herplanTemplate != null)
                    return BerichtResponseGenerator.BouwAangepasteAntwoord(herplanTemplate, classificatie, bericht, clubSettings);

                var herplanOpties = herplanData["herplanOpties"]?.ToObject<HerplanCheckResponse>();
                return BerichtResponseGenerator.BouwHerplanAntwoord(
                    wedstrijd, herplanOpties, classificatie, bericht, clubSettings);

            case VerzoekType.TeamContactOpvragen:
                var teamContactTemplate = await Email.EmailTemplateService.GetTemplateAsync("team_contact_opvragen", log);
                if (teamContactTemplate != null)
                    return BerichtResponseGenerator.BouwAangepasteAntwoord(teamContactTemplate, classificatie, bericht, clubSettings);
                return BerichtResponseGenerator.BouwTeamContactAutoReply(classificatie, bericht, clubSettings);

            case VerzoekType.Bevestiging:
                var bevestigingTemplate = await Email.EmailTemplateService.GetTemplateAsync("bevestiging", log);
                if (bevestigingTemplate != null)
                    return BerichtResponseGenerator.BouwAangepasteAntwoord(bevestigingTemplate, classificatie, bericht, clubSettings);
                return BerichtResponseGenerator.BouwBevestigingAntwoord(bericht, classificatie, clubSettings);

            default:
                var buitenScopeTemplate = await Email.EmailTemplateService.GetTemplateAsync("buiten_scope", log);
                if (buitenScopeTemplate != null)
                    return BerichtResponseGenerator.BouwAangepasteAntwoord(buitenScopeTemplate, classificatie, bericht, clubSettings);
                return BerichtResponseGenerator.BouwBuitenScopeAntwoord(bericht, clubSettings);
        }
    }

    // ── Private helpers ──

    /// <summary>
    /// Als de berichttekst 'doordeweeks' bevat, vervang de AI-datums door de exacte
    /// maandag t/m donderdag van de week die de AI afleidde. Vrijdag is nooit doordeweeks.
    /// Een concreet gevraagde datum blijft altijd staan en datums in het verleden vallen af.
    /// </summary>
    internal static List<string> ExpandDoordeweeksDatums(
        List<string> aiDatums, string onderwerp, string body)
    {
        var eigenBody = StripCitaatEnOndertekening(body);
        var tekst = (onderwerp + " " + eigenBody).ToLowerInvariant();
        if (!tekst.Contains("doordeweeks"))
            return aiDatums;

        // "doordeweeks, bijvoorbeeld woensdag 13 mei" is een concreet verzoek — dat mag niet door
        // vier andere dagen worden vervangen.
        if (ExtractExpliciteDatum(onderwerp).HasValue || ExtractExpliciteDatum(eigenBody).HasValue)
            return aiDatums;

        // Leid de weekmaandag af: óf uit de eerste AI-datum, óf uit "volgende week"
        DateOnly weekStart;
        if (aiDatums.Count > 0 && DateOnly.TryParse(aiDatums[0], out var firstDate))
        {
            // Rol terug naar de maandag van de week van die datum
            int daysFromMonday = ((int)firstDate.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            weekStart = firstDate.AddDays(-daysFromMonday);
        }
        else
        {
            // Geen AI-datum: neem de volgende kalenderweek
            var today = DateOnly.FromDateTime(DateTime.Today);
            int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
            if (daysUntilMonday == 0) daysUntilMonday = 7;
            weekStart = today.AddDays(daysUntilMonday);
        }

        // Maandag t/m donderdag (4 dagen), zonder de dagen die al voorbij zijn — anders krijgt de
        // afzender voor elke verstreken dag een "datum moet in de toekomst zijn"-antwoord.
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        return Enumerable.Range(0, 4)
            .Select(i => weekStart.AddDays(i))
            .Where(d => d > vandaag)
            .Select(d => d.ToString("yyyy-MM-dd"))
            .ToList();
    }

    /// <summary>
    /// De datum waarover een enkelvoudig verzoek gaat: de eerste datum uit de (mogelijk door
    /// <see cref="ExpandDoordeweeksDatums"/> aangepaste) lijst, zodat plannercheck, tegenstander-lookup
    /// en antwoord altijd dezelfde datum gebruiken. Valt terug op de AI-datum als de lijst leeg is.
    /// </summary>
    internal static string? KiesPrimaireDatum(List<string> alleDatums, string? aiDatum)
        => alleDatums.Count > 0 ? alleDatums[0] : aiDatum;

    /// <summary>
    /// Knipt de tekst af bij de eerste citaat- of doorstuurkop, zodat alleen de eigen tekst van de
    /// afzender wordt geanalyseerd. Blijft er niets over (het bericht is volledig een citaat), dan
    /// is de originele tekst het enige beschikbare materiaal en wordt die teruggegeven.
    /// </summary>
    internal static string StripCitaatEnOndertekening(string tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst)) return tekst ?? "";

        var markers = new[]
        {
            @"\bvan\s*:",
            @"\bverzonden\s*:",
            @"\bfrom\s*:",
            @"\bsent\s*:",
            @"-{2,}\s*original",
            @"\bop\b[^\n]{0,120}?\bschreef\b"
        };

        var eerstePositie = -1;
        foreach (var marker in markers)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                tekst, marker, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && (eerstePositie < 0 || match.Index < eerstePositie))
                eerstePositie = match.Index;
        }

        if (eerstePositie < 0) return tekst;

        var eigenTekst = tekst[..eerstePositie];
        return string.IsNullOrWhiteSpace(eigenTekst) ? tekst : eigenTekst;
    }

    private static bool HeeftReplyPrefix(string onderwerp)
        => !string.IsNullOrWhiteSpace(onderwerp)
           && System.Text.RegularExpressions.Regex.IsMatch(
               onderwerp, @"^\s*(?:(?:re|fw|fwd|aw)\s*:\s*)+",
               System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Een maandnaam zonder jaartal betekent "het eerstvolgende voorkomen". Zonder deze regel
    /// levert "10 januari" in een mail van half december een datum van elf maanden terug op,
    /// waarop de afzender automatisch "datum moet in de toekomst zijn" terugkrijgt.
    /// Een datum die nog maar kort geleden is, blijft in het huidige jaar: dat is vaker een
    /// verwijzing naar het recente verleden dan naar volgend jaar.
    /// </summary>
    private static DateOnly? EerstvolgendVoorkomen(int dag, int maand)
    {
        const int verledenTolerantieDagen = 30;
        var ondergrens = DateOnly.FromDateTime(DateTime.Today).AddDays(-verledenTolerantieDagen);

        for (int jaarOffset = 0; jaarOffset <= 1; jaarOffset++)
        {
            DateOnly kandidaat;
            try { kandidaat = new DateOnly(DateTime.Today.Year + jaarOffset, maand, dag); }
            catch { continue; }
            if (kandidaat >= ondergrens) return kandidaat;
        }
        return null;
    }

    private static DateOnly? ExtractExpliciteDatum(string tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst)) return null;

        var numericMatch = System.Text.RegularExpressions.Regex.Match(tekst, @"(\d{1,2})-(\d{1,2})-(\d{4})");
        if (numericMatch.Success)
        {
            if (int.TryParse(numericMatch.Groups[1].Value, out var dag) &&
                int.TryParse(numericMatch.Groups[2].Value, out var maand) &&
                int.TryParse(numericMatch.Groups[3].Value, out var jaar))
            {
                try { return new DateOnly(jaar, maand, dag); } catch { }
            }
        }

        var maandNamen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["januari"] = 1, ["februari"] = 2, ["maart"] = 3, ["april"] = 4,
            ["mei"] = 5, ["juni"] = 6, ["juli"] = 7, ["augustus"] = 8,
            ["september"] = 9, ["oktober"] = 10, ["november"] = 11, ["december"] = 12
        };

        var tekstLower = tekst.ToLowerInvariant();
        foreach (var (naam, maandNr) in maandNamen)
        {
            var maandMatch = System.Text.RegularExpressions.Regex.Match(tekstLower, $@"(\d{{1,2}})\s+{naam}(?:\s+(\d{{4}}))?");
            if (maandMatch.Success && int.TryParse(maandMatch.Groups[1].Value, out var d))
            {
                if (maandMatch.Groups[2].Success && int.TryParse(maandMatch.Groups[2].Value, out var expliciteJaar))
                {
                    try { return new DateOnly(expliciteJaar, maandNr, d); } catch { }
                }
                else
                {
                    var kandidaat = EerstvolgendVoorkomen(d, maandNr);
                    if (kandidaat.HasValue) return kandidaat;
                }
            }
        }

        return null;
    }

    private static string? ExtractEigenTeamUitWedstrijd(string wedstrijd, string tegenstander, string? clubCodeOverride = null)
    {
        var clubPrefix = ResolveHeuristicClubCode(clubCodeOverride) + " ";
        var parts = wedstrijd.Split(" - ", 2, StringSplitOptions.TrimEntries);
        foreach (var part in parts)
            if (!string.IsNullOrWhiteSpace(clubPrefix.Trim())
                && part.StartsWith(clubPrefix, StringComparison.OrdinalIgnoreCase)
                && !part.Contains(tegenstander, StringComparison.OrdinalIgnoreCase))
                return part;
        foreach (var part in parts)
            if (!part.Contains(tegenstander, StringComparison.OrdinalIgnoreCase))
                return part;
        return null;
    }

    /// <summary>
    /// Club-heuristiek-resolver voor niet-SQL "eigen team"-herkenning (#677). Een expliciet
    /// meegegeven clubCode-override (dry-run vanuit de GUI-clubswitcher, bijv. AllStars FC) heeft
    /// voorrang boven de proces-globale AppSettings-cache. Gebruik dit NOOIT voor SQL-filters —
    /// daarvoor gaat de clubCode al rechtstreeks als parameter naar PlannerDataAccess/PlannerService.
    /// </summary>
    private static string ResolveHeuristicClubCode(string? clubCodeOverride)
        => !string.IsNullOrWhiteSpace(clubCodeOverride) ? clubCodeOverride : SystemUtilities.AppSettings.GetOptionalClubCode();

    /// <summary>
    /// Past de teamnaam→TeamId-vertaallaag toe volgens <c>TeamResolverMode</c> (#698/#699).
    ///
    /// <list type="bullet">
    ///   <item><description><c>off</c> of geen context → <paramref name="huidigeTeamNaam"/> onveranderd terug.</description></item>
    ///   <item><description><c>shadow</c> → alleen vergelijken en loggen, uitkomst onveranderd.</description></item>
    ///   <item><description><c>on</c> → de canonieke teamnaam wordt leidend, mits eenduidig opgelost.</description></item>
    /// </list>
    ///
    /// Faalt nooit naar buiten: kan de vertaallaag niets zinnigs opleveren, dan blijft de bestaande
    /// uitkomst staan. Dat maakt de uitrol omkeerbaar zonder codewijziging.
    /// </summary>
    private static async Task<string?> PasTeamResolutieToeAsync(
        TeamResolutieContext? context, string ruweTeamTekst, string? huidigeTeamNaam,
        string? clubCode, ILogger log)
    {
        if (context is null) return huidigeTeamNaam;

        var modus = context.Modus;
        if (modus == TeamResolverMode.Off || string.IsNullOrWhiteSpace(ruweTeamTekst))
            return huidigeTeamNaam;

        var cc = ResolveHeuristicClubCode(clubCode);
        if (string.IsNullOrWhiteSpace(cc)) return huidigeTeamNaam;

        if (modus == TeamResolverMode.Shadow)
        {
            await context.ShadowLogger.VergelijkAsync(ruweTeamTekst, huidigeTeamNaam, cc);
            return huidigeTeamNaam;
        }

        try
        {
            var resultaat = await context.Resolver.ResolveAsync(
                new TeamResolutionRequest(ruweTeamTekst, null, null, cc));

            if (resultaat.TeamId is null || string.IsNullOrWhiteSpace(resultaat.CanoniekeTeamnaam))
            {
                log.LogInformation(
                    "TEAMRESOLUTIE - niet eenduidig opgelost (bron={Bron}, kandidaten={Kandidaten}) — bestaande matching blijft leidend",
                    resultaat.Bron, resultaat.Kandidaten.Count);
                return huidigeTeamNaam;
            }

            log.LogInformation(
                "TEAMRESOLUTIE - teamId={TeamId} bron={Bron} confidence={Confidence}",
                resultaat.TeamId, resultaat.Bron, resultaat.Confidence);
            return resultaat.CanoniekeTeamnaam;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TEAMRESOLUTIE - mislukt, bestaande matching blijft leidend");
            return huidigeTeamNaam;
        }
    }

    /// <summary>
    /// Normaliseert en prefixt een teamnaam met de club-code. <paramref name="clubCodeOverride"/>
    /// (#677) laat het dry-run pad de in de GUI geselecteerde club (bijv. AllStars FC) gebruiken
    /// in plaats van de proces-globale cache, die altijd de primaire club van deze deployment bevat.
    /// </summary>
    internal static string? NormaliseerTeamNaam(string? teamNaam, string? clubCodeOverride = null)
    {
        if (string.IsNullOrWhiteSpace(teamNaam)) return teamNaam;
        var t = teamNaam.Trim();

        t = System.Text.RegularExpressions.Regex.Replace(t, @"(\d)\s*/\s*(\d)", "$1-$2");

        if (t.StartsWith("Onder ", StringComparison.OrdinalIgnoreCase))
            t = "JO" + t[6..].Trim();

        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^O\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !t.StartsWith("MO", StringComparison.OrdinalIgnoreCase))
            t = "J" + t.ToUpper();

        bool looksLikeEigenTeam = System.Text.RegularExpressions.Regex.IsMatch(t, @"^(JO|MO|VR|JM|ZO)\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                              || !t.Contains(' ');
        var clubCode = !string.IsNullOrWhiteSpace(clubCodeOverride) ? clubCodeOverride : SystemUtilities.AppSettings.GetSetting("clubCode");
        if (!string.IsNullOrWhiteSpace(clubCode))
        {
            var clubPrefix = clubCode + " ";
            if (looksLikeEigenTeam && !t.StartsWith(clubPrefix, StringComparison.OrdinalIgnoreCase))
                t = clubPrefix + t;
        }

        return t;
    }

    private static string? NormaliseerLeeftijdsCategorie(string? categorie)
    {
        if (string.IsNullOrWhiteSpace(categorie)) return categorie;
        var c = categorie.Trim();
        if (c.StartsWith("Onder ", StringComparison.OrdinalIgnoreCase))
            c = "JO" + c[6..].Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(c, @"^O\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !c.StartsWith("MO", StringComparison.OrdinalIgnoreCase))
            c = "J" + c.ToUpper();
        return c;
    }

    private static string? DetecteerRichting(string onderwerp, string body)
    {
        var tekst = ((onderwerp ?? "") + " " + (body ?? "")).ToLowerInvariant();
        bool vervroegen = tekst.Contains("vervroeg") || tekst.Contains("eerder")
                       || tekst.Contains("naar voren");
        bool verlaten = tekst.Contains("verlaat") || tekst.Contains("verlat")
                     || tekst.Contains(" later") || tekst.Contains("naar achter");
        if (vervroegen && !verlaten) return "vervroegen";
        if (verlaten && !vervroegen) return "verlaten";
        return null;
    }
}
