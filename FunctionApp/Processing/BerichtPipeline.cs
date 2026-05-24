using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SportlinkFunction.Email;
using SportlinkFunction.Planner;

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
    /// </summary>
    public static void ValideerDagDatum(BerichtClassificatie classificatie, string emailBody, string onderwerp)
    {
        var onderwerpDatum = ExtractExpliciteDatum(onderwerp);
        if (onderwerpDatum.HasValue)
        {
            classificatie.Datum = onderwerpDatum.Value.ToString("yyyy-MM-dd");
            return;
        }

        var bodyDatum = ExtractExpliciteDatum(emailBody);
        if (bodyDatum.HasValue && string.IsNullOrEmpty(classificatie.Datum))
        {
            classificatie.Datum = bodyDatum.Value.ToString("yyyy-MM-dd");
            return;
        }

        if (string.IsNullOrEmpty(classificatie.Datum)) return;
        if (!DateOnly.TryParse(classificatie.Datum, out var datum)) return;

        var tekst = (onderwerp + " " + emailBody).ToLowerInvariant();
        var dagNamen = new (string naam, DayOfWeek dag)[]
        {
            ("maandag", DayOfWeek.Monday), ("dinsdag", DayOfWeek.Tuesday),
            ("woensdag", DayOfWeek.Wednesday), ("donderdag", DayOfWeek.Thursday),
            ("vrijdag", DayOfWeek.Friday), ("zaterdag", DayOfWeek.Saturday),
            ("zondag", DayOfWeek.Sunday)
        };

        foreach (var (naam, dag) in dagNamen)
        {
            if (!tekst.Contains(naam)) continue;
            if (datum.DayOfWeek == dag) return;

            for (int offset = 1; offset <= 7; offset++)
            {
                if (datum.AddDays(-offset).DayOfWeek == dag)
                    { classificatie.Datum = datum.AddDays(-offset).ToString("yyyy-MM-dd"); return; }
                if (datum.AddDays(offset).DayOfWeek == dag)
                    { classificatie.Datum = datum.AddDays(offset).ToString("yyyy-MM-dd"); return; }
            }
            return;
        }
    }

    /// <summary>
    /// Vertaalt de AI-classificatie naar de juiste PlannerService-aanroep.
    /// </summary>
    public static async Task<string> VerwerkMetPlannerAsync(
        BerichtClassificatie classificatie, InkomendBericht bericht, ILogger log)
    {
        classificatie.LeeftijdsCategorie = NormaliseerLeeftijdsCategorie(classificatie.LeeftijdsCategorie);

        var team = classificatie.TeamNaam ?? "";
        var tegenstander = classificatie.Tegenstander ?? "";

        if (!string.IsNullOrWhiteSpace(team) && !string.IsNullOrWhiteSpace(tegenstander))
        {
            var cc = SystemUtilities.AppSettings.GetSetting("clubCode") ?? "";
            bool teamIsEigenClub = !team.Contains(' ')
                || (!string.IsNullOrWhiteSpace(cc) && team.StartsWith(cc, StringComparison.OrdinalIgnoreCase));
            bool tegenstanderIsEigenClub = !tegenstander.Contains(' ')
                || (!string.IsNullOrWhiteSpace(cc) && tegenstander.StartsWith(cc, StringComparison.OrdinalIgnoreCase));

            if (!teamIsEigenClub && tegenstanderIsEigenClub)
            {
                classificatie.TeamNaam = tegenstander;
                classificatie.Tegenstander = team;
            }
        }

        classificatie.TeamNaam = NormaliseerTeamNaam(classificatie.TeamNaam);

        switch (classificatie.Type)
        {
            case VerzoekType.BeschikbaarheidCheck:
                var alleDatums = ExpandDoordeweeksDatums(
                    classificatie.GetAlleDatums(), bericht.Onderwerp, bericht.Body);

                var cc2 = SystemUtilities.AppSettings.GetSetting("clubCode") ?? "";
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
                        classificatie.Tegenstander!, opponentCheckDatum);
                    if (wedstrijdOpDatum != null)
                        return JsonConvert.SerializeObject(new { wedstrijdAlIngepland = true, wedstrijd = wedstrijdOpDatum });

                    var wedstrijdAndereDatum = await PlannerDataAccess.FindMatchByOpponentAsync(
                        classificatie.Tegenstander!, null);
                    if (wedstrijdAndereDatum == null)
                        return JsonConvert.SerializeObject(new { teamOnbekend = true, tegenstander = classificatie.Tegenstander });

                    var vrcTeam = ExtractEigenTeamUitWedstrijd(wedstrijdAndereDatum.Wedstrijd, classificatie.Tegenstander!);
                    if (vrcTeam != null)
                        classificatie.TeamNaam = NormaliseerTeamNaam(vrcTeam);
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
                            Tegenstander = classificatie.Tegenstander
                        };
                        var resp = await PlannerService.CheckAvailabilityAsync(req, log);
                        multiResults.Add(new { datum, response = resp });
                    }
                    return JsonConvert.SerializeObject(new { multiDatum = true, resultaten = multiResults });
                }
                var checkRequest = new CheckAvailabilityRequest
                {
                    Datum = classificatie.Datum ?? "",
                    AanvangsTijd = classificatie.AanvangsTijd,
                    LeeftijdsCategorie = classificatie.LeeftijdsCategorie,
                    TeamNaam = classificatie.TeamNaam,
                    Tegenstander = classificatie.Tegenstander
                };
                var checkResponse = await PlannerService.CheckAvailabilityAsync(checkRequest, log);
                return JsonConvert.SerializeObject(checkResponse);

            case VerzoekType.HerplanVerzoek:
                if (!string.IsNullOrEmpty(classificatie.TeamNaam) && !string.IsNullOrEmpty(classificatie.Datum))
                {
                    if (DateOnly.TryParse(classificatie.Datum, out var datum))
                    {
                        var wedstrijd = await PlannerDataAccess.FindMatchAsync(classificatie.TeamNaam, datum);
                        if (wedstrijd != null)
                        {
                            var deadlineDagen = int.TryParse(SystemUtilities.AppSettings.GetSetting("herplanDeadlineDagen"), out var dd) ? dd : 8;
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
                                var beschikbaarheid = await PlannerService.CheckAvailabilityAsync(gewenstRequest, log);
                                return JsonConvert.SerializeObject(new { wedstrijd, gewensteDatum = classificatie.GewensteDatum, beschikbaarheid });
                            }

                            var herplanRequest = new HerplanCheckRequest
                            {
                                Wedstrijdcode = wedstrijd.Wedstrijdcode,
                                VoorkeurTijd = classificatie.AanvangsTijd,
                                Richting = DetecteerRichting(bericht.Onderwerp, bericht.Body)
                            };
                            var herplanResponse = await PlannerService.CheckRescheduleAvailabilityAsync(herplanRequest, log);
                            return JsonConvert.SerializeObject(new { wedstrijd, herplanOpties = herplanResponse });
                        }
                        return JsonConvert.SerializeObject(new { gevonden = false, reden = $"Geen wedstrijd gevonden voor {classificatie.TeamNaam} op {classificatie.Datum}" });
                    }
                }
                return JsonConvert.SerializeObject(new { error = "Onvoldoende gegevens voor herplanverzoek (team en datum nodig)" });

            case VerzoekType.TeamContactOpvragen:
                if (!string.IsNullOrWhiteSpace(classificatie.TeamNaam))
                {
                    var contact = await PlannerDataAccess.GetTeamleiderContactAsync(classificatie.TeamNaam);
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
    /// </summary>
    public static (string onderwerp, string body) BouwTemplateAntwoord(
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        InkomendBericht bericht)
    {
        switch (classificatie.Type)
        {
            case VerzoekType.BeschikbaarheidCheck:
                var jobj = Newtonsoft.Json.Linq.JObject.Parse(plannerResponseJson);

                if (jobj["wedstrijdAlIngepland"]?.ToObject<bool>() == true)
                {
                    var ingeplandWedstrijd = jobj["wedstrijd"]?.ToObject<ZoekWedstrijdResponse>();
                    return BerichtResponseGenerator.BouwWedstrijdAlIngeplandAntwoord(
                        ingeplandWedstrijd, classificatie, bericht);
                }

                if (jobj["teamOnbekend"]?.ToObject<bool>() == true)
                {
                    var onbekendeTegenstander = jobj["tegenstander"]?.ToString()
                        ?? classificatie.Tegenstander ?? "";
                    return BerichtResponseGenerator.BouwTeamOnbekendAntwoord(
                        onbekendeTegenstander, classificatie, bericht);
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
                        resultaten, classificatie, bericht);
                }
                var checkResponse = JsonConvert.DeserializeObject<CheckAvailabilityResponse>(plannerResponseJson);
                return BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
                    checkResponse ?? new CheckAvailabilityResponse(), classificatie, bericht);

            case VerzoekType.HerplanVerzoek:
                var herplanData = Newtonsoft.Json.Linq.JObject.Parse(plannerResponseJson);
                var wedstrijd = herplanData["wedstrijd"]?.ToObject<ZoekWedstrijdResponse>();

                if (herplanData["herplanTeLaat"]?.ToObject<bool>() == true)
                {
                    var teLaatWedstrijd = herplanData["wedstrijd"]?.ToObject<ZoekWedstrijdResponse>();
                    var deadlineDagen = herplanData["deadlineDagen"]?.ToObject<int>() ?? 8;
                    var dagenTot = herplanData["dagenTotWedstrijd"]?.ToObject<int>() ?? 0;
                    return BerichtResponseGenerator.BouwHerplanTeLaatAntwoord(teLaatWedstrijd, deadlineDagen, dagenTot, classificatie, bericht);
                }

                if (herplanData["gewensteDatum"] != null && herplanData["beschikbaarheid"] != null)
                {
                    var gewensteDatum = herplanData["gewensteDatum"]?.ToString();
                    var beschikbaarheid = herplanData["beschikbaarheid"]?.ToObject<CheckAvailabilityResponse>();
                    return BerichtResponseGenerator.BouwHerplanGewensteDatumAntwoord(
                        wedstrijd, gewensteDatum, beschikbaarheid, classificatie, bericht);
                }

                var herplanOpties = herplanData["herplanOpties"]?.ToObject<HerplanCheckResponse>();
                return BerichtResponseGenerator.BouwHerplanAntwoord(
                    wedstrijd, herplanOpties, classificatie, bericht);

            case VerzoekType.TeamContactOpvragen:
                return BerichtResponseGenerator.BouwTeamContactAutoReply(classificatie, bericht);

            case VerzoekType.Bevestiging:
                return BerichtResponseGenerator.BouwBevestigingAntwoord(bericht, classificatie);

            default:
                return BerichtResponseGenerator.BouwBuitenScopeAntwoord(bericht);
        }
    }

    // ── Private helpers ──

    /// <summary>
    /// Als de berichttekst 'doordeweeks' bevat, vervang de AI-datums door de exacte
    /// maandag t/m donderdag van de week die de AI afleidde. Vrijdag is nooit doordeweeks.
    /// </summary>
    private static List<string> ExpandDoordeweeksDatums(
        List<string> aiDatums, string onderwerp, string body)
    {
        var tekst = (onderwerp + " " + body).ToLowerInvariant();
        if (!tekst.Contains("doordeweeks"))
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

        // Maandag t/m donderdag (4 dagen)
        return Enumerable.Range(0, 4)
            .Select(i => weekStart.AddDays(i).ToString("yyyy-MM-dd"))
            .ToList();
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
                var j = maandMatch.Groups[2].Success && int.TryParse(maandMatch.Groups[2].Value, out var jj)
                    ? jj : DateTime.Now.Year;
                try { return new DateOnly(j, maandNr, d); } catch { }
            }
        }

        return null;
    }

    private static string? ExtractEigenTeamUitWedstrijd(string wedstrijd, string tegenstander)
    {
        var clubPrefix = (SystemUtilities.AppSettings.GetSetting("clubCode") ?? "") + " ";
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

    private static string? NormaliseerTeamNaam(string? teamNaam)
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
        var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode");
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
