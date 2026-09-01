using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Planner.Repositories;
using FunctionApp.Postgres.TeamResolution;

namespace FunctionApp.Postgres.Processing;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Processing/BerichtPipeline.cs</c> (#889). Kanaal-
/// agnostische verwerkingspipeline voor inkomende berichten — hier uitsluitend geoefend door het
/// dry-run pad (<c>EmailTestFunction</c>); <c>EmailProcessorFunction</c> (de echte, mailbox-
/// getriggerde pipeline) is op deze tier niet vertaald.
///
/// <para>
/// <b>Drie bewuste, gedocumenteerde afwijkingen ten opzichte van het SQL Server-origineel</b> —
/// geen stille functionaliteitsreductie, maar dezelfde eerlijke terugval die het origineel zelf al
/// gebruikt zodra de bijbehorende instelling/repository ontbreekt:
/// </para>
/// <list type="number">
/// <item>Het "opponent kan ons team alsnog vinden"-pad (<c>PlannerDataAccess.FindMatchByOpponentAsync</c>)
/// is niet vertaald. Onze eigen team niet herkend + wel een tegenstander genoemd geeft hier direct
/// "team onbekend" — het origineel probeert eerst via de tegenstander te resolven. Aparte,
/// afgebakende vervolgklus.</item>
/// <item><c>TeamContactOpvragen</c> geeft hier altijd <c>coachGevonden = false</c>:
/// <c>PlannerDataAccess.GetTeamleiderContactAsync</c>/<c>AllstarsTestDataRepository.GetTeamleiderContactAsync</c>
/// zijn niet vertaald (al expliciet zo gedocumenteerd in <c>AllstarsTestDataRepository.cs</c> op
/// deze tier). Nooit gegokt of stilzwijgend "gevonden" gemeld.</item>
/// <item>Het "verzet zonder datum"-pad (#561, KNVB-bijlage + vrije-zaterdagen-voorzet) valt hier
/// altijd terug op het standaard herplanpad — exact het bestaande fallbackgedrag van het origineel
/// zodra <c>knvbStandaardRegio</c> ontbreekt. Op deze tier ontbreekt die instelling altijd (niet in
/// <see cref="PostgresAppSettings"/> geladen), dus <c>KnvbKalenderRepository</c> en een
/// <c>SeasonHelper</c>-tegenhanger zijn (nog) niet nodig.</item>
/// </list>
/// </summary>
internal static class BerichtPipeline
{
    /// <summary>
    /// Extraheert datums uit onderwerp en body, en corrigeert de AI-classificatie.
    /// Prioriteit: expliciete datum in onderwerp > expliciete datum in body > AI datum + dag-validatie.
    /// In een reply-thread wint de AI-datum boven de (oude) datum in het onderwerp.
    /// </summary>
    internal static void ValideerDagDatum(BerichtClassificatie classificatie, string emailBody, string onderwerp)
    {
        var eigenTekst = StripCitaatEnOndertekening(emailBody);

        var onderwerpDatum = ExtractExpliciteDatum(onderwerp);
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
    /// Vertaalt de AI-classificatie naar de juiste Availability-/RescheduleService-aanroep.
    /// </summary>
    /// <param name="clubCode">
    /// Expliciete club-override (#677) — gebruikt door het dry-run pad (EmailTestFunction) om de
    /// GUI-clubswitcher (bijv. AllStars FC) te respecteren.
    /// </param>
    /// <param name="teamResolver">
    /// Teamnaam→TeamId-vertaallaag (#692/#889). Enige pad waarlangs een team wordt herkend.
    /// </param>
    internal static async Task<string> VerwerkMetPlannerAsync(
        BerichtClassificatie classificatie, InkomendBericht bericht, ILogger log,
        ITeamResolver teamResolver,
        string? clubCode = null, ClubAppSettingsSnapshot? clubSettings = null)
    {
        ArgumentNullException.ThrowIfNull(teamResolver);

        var cs = PostgresDatabaseConfig.ConnectionString;
        var cc = ResolveHeuristicClubCode(clubCode);
        var eigenTeamHerkend = await BepaalEigenTeamEnTegenstanderAsync(classificatie, teamResolver, cc, log);

        switch (classificatie.Type)
        {
            case VerzoekType.BeschikbaarheidCheck:
                var alleDatums = ExpandDoordeweeksDatums(
                    classificatie.GetAlleDatums(), bericht.Onderwerp, bericht.Body ?? "");

                // #889: het "opponent kan ons team alsnog vinden"-pad (FindMatchByOpponentAsync) is
                // hier niet vertaald — zie de klassekop. Eigen team niet herkend + wel een
                // tegenstander genoemd valt hier direct door naar de gewone beschikbaarheidscheck.

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
                        var resp = await AvailabilityService.CheckAvailabilityAsync(cs, req, log, clubCode);
                        multiResults.Add(new { datum, response = resp });
                    }
                    return JsonConvert.SerializeObject(new { multiDatum = true, resultaten = multiResults });
                }
                var primaireDatum = KiesPrimaireDatum(alleDatums, classificatie.Datum);
                if (string.IsNullOrWhiteSpace(primaireDatum) || !DateOnly.TryParse(primaireDatum, out _))
                {
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
                var checkResponse = await AvailabilityService.CheckAvailabilityAsync(cs, checkRequest, log, clubCode);
                return JsonConvert.SerializeObject(checkResponse);

            case VerzoekType.HerplanVerzoek:
                if (!string.IsNullOrEmpty(classificatie.TeamNaam) && !string.IsNullOrEmpty(classificatie.Datum))
                {
                    if (DateOnly.TryParse(classificatie.Datum, out var datum))
                    {
                        var wedstrijd = await PlannerMatchRepository.FindMatchAsync(cs, classificatie.TeamNaam, datum, clubCode);
                        if (wedstrijd != null)
                        {
                            var deadlineDagen = clubSettings?.HerplanDeadlineDagen
                                ?? (int.TryParse(PostgresAppSettings.GetSetting("herplanDeadlineDagen"), out var dd) ? dd : 8);
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

                            // #561/#889: "verzet zonder datum"-pad — zie de klassekop, altijd null
                            // op deze tier, dus rechtstreeks door naar het standaard herplanpad.

                            if (!string.IsNullOrEmpty(classificatie.GewensteDatum))
                            {
                                var gewenstRequest = new CheckAvailabilityRequest
                                {
                                    Datum = classificatie.GewensteDatum,
                                    LeeftijdsCategorie = classificatie.LeeftijdsCategorie,
                                    TeamNaam = classificatie.TeamNaam
                                };
                                var beschikbaarheid = await AvailabilityService.CheckAvailabilityAsync(cs, gewenstRequest, log, clubCode);
                                return JsonConvert.SerializeObject(new { wedstrijd, gewensteDatum = classificatie.GewensteDatum, beschikbaarheid });
                            }

                            var herplanRequest = new HerplanCheckRequest
                            {
                                Wedstrijdcode = wedstrijd.Wedstrijdcode,
                                VoorkeurTijd = classificatie.AanvangsTijd,
                                Richting = DetecteerRichting(bericht.Onderwerp, bericht.Body ?? "")
                            };
                            var herplanResponse = await RescheduleService.CheckRescheduleAvailabilityAsync(cs, herplanRequest, log, clubCode);
                            return JsonConvert.SerializeObject(new { wedstrijd, herplanOpties = herplanResponse });
                        }
                        return JsonConvert.SerializeObject(new { gevonden = false, reden = $"Geen wedstrijd gevonden voor {classificatie.TeamNaam} op {classificatie.Datum}" });
                    }
                }
                return JsonConvert.SerializeObject(new { error = "Onvoldoende gegevens voor herplanverzoek (team en datum nodig)" });

            case VerzoekType.TeamContactOpvragen:
                // #889: GetTeamleiderContactAsync is op deze tier niet vertaald — zie de klassekop.
                return JsonConvert.SerializeObject(new
                {
                    teamContactOpgevraagd = true,
                    teamNaam = classificatie.TeamNaam,
                    coachGevonden = false
                });

            case VerzoekType.Bevestiging:
                return JsonConvert.SerializeObject(new { status = "Bevestiging ontvangen", opmerking = "Bevestigingen vereisen handmatige afhandeling door de coördinator" });

            default:
                return JsonConvert.SerializeObject(new { status = "Niet verwerkt" });
        }
    }

    /// <summary>
    /// Bouwt het antwoord via templates op basis van het classificatietype en response.
    /// Probeert eerst een DB-override op te halen via EmailTemplateService (#287).
    /// Valt terug op hardcoded defaults als er geen override is.
    /// </summary>
    internal static async Task<(string onderwerp, string body)> BouwTemplateAntwoord(
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        InkomendBericht bericht,
        ILogger? log = null,
        ClubAppSettingsSnapshot? clubSettings = null,
        string? clubCode = null)
    {
        switch (classificatie.Type)
        {
            case VerzoekType.BeschikbaarheidCheck:
                var jobj = Newtonsoft.Json.Linq.JObject.Parse(plannerResponseJson);

                if (jobj["teamOnbekend"]?.ToObject<bool>() == true)
                {
                    var onbekendeTegenstander = jobj["tegenstander"]?.ToString()
                        ?? classificatie.Tegenstander ?? "";
                    return BerichtResponseGenerator.BouwTeamOnbekendAntwoord(
                        onbekendeTegenstander, classificatie, bericht, clubSettings);
                }

                if (jobj["datumOnbekend"]?.ToObject<bool>() == true)
                {
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

                var beschikbaarheidTemplate = await Email.EmailTemplateService.GetTemplateAsync("beschikbaarheid_check", clubCode, log);
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

                var herplanTemplate = await Email.EmailTemplateService.GetTemplateAsync("herplan_verzoek", clubCode, log);
                if (herplanTemplate != null)
                    return BerichtResponseGenerator.BouwAangepasteAntwoord(herplanTemplate, classificatie, bericht, clubSettings);

                var herplanOpties = herplanData["herplanOpties"]?.ToObject<HerplanCheckResponse>();
                return BerichtResponseGenerator.BouwHerplanAntwoord(
                    wedstrijd, herplanOpties, classificatie, bericht, clubSettings);

            case VerzoekType.TeamContactOpvragen:
                var teamContactTemplate = await Email.EmailTemplateService.GetTemplateAsync("team_contact_opvragen", clubCode, log);
                if (teamContactTemplate != null)
                    return BerichtResponseGenerator.BouwAangepasteAntwoord(teamContactTemplate, classificatie, bericht, clubSettings);
                return BerichtResponseGenerator.BouwTeamContactAutoReply(classificatie, bericht, clubSettings);

            case VerzoekType.Bevestiging:
                var bevestigingTemplate = await Email.EmailTemplateService.GetTemplateAsync("bevestiging", clubCode, log);
                if (bevestigingTemplate != null)
                    return BerichtResponseGenerator.BouwAangepasteAntwoord(bevestigingTemplate, classificatie, bericht, clubSettings);
                return BerichtResponseGenerator.BouwBevestigingAntwoord(bericht, classificatie, clubSettings);

            default:
                var buitenScopeTemplate = await Email.EmailTemplateService.GetTemplateAsync("buiten_scope", clubCode, log);
                if (buitenScopeTemplate != null)
                    return BerichtResponseGenerator.BouwAangepasteAntwoord(buitenScopeTemplate, classificatie, bericht, clubSettings);
                return BerichtResponseGenerator.BouwBuitenScopeAntwoord(bericht, clubSettings);
        }
    }

    // ── Private helpers ──

    internal static List<string> ExpandDoordeweeksDatums(
        List<string> aiDatums, string onderwerp, string body)
    {
        var eigenBody = StripCitaatEnOndertekening(body);
        var tekst = (onderwerp + " " + eigenBody).ToLowerInvariant();
        if (!tekst.Contains("doordeweeks"))
            return aiDatums;

        if (ExtractExpliciteDatum(onderwerp).HasValue || ExtractExpliciteDatum(eigenBody).HasValue)
            return aiDatums;

        DateOnly weekStart;
        if (aiDatums.Count > 0 && DateOnly.TryParse(aiDatums[0], out var firstDate))
        {
            int daysFromMonday = ((int)firstDate.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            weekStart = firstDate.AddDays(-daysFromMonday);
        }
        else
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
            if (daysUntilMonday == 0) daysUntilMonday = 7;
            weekStart = today.AddDays(daysUntilMonday);
        }

        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        return Enumerable.Range(0, 4)
            .Select(i => weekStart.AddDays(i))
            .Where(d => d > vandaag)
            .Select(d => d.ToString("yyyy-MM-dd"))
            .ToList();
    }

    internal static string? KiesPrimaireDatum(List<string> alleDatums, string? aiDatum)
        => alleDatums.Count > 0 ? alleDatums[0] : aiDatum;

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

        var slashMatchMetJaar = System.Text.RegularExpressions.Regex.Match(tekst, @"(?<![\d/])(\d{1,2})/(\d{1,2})/(\d{4})(?!\d)");
        if (slashMatchMetJaar.Success)
        {
            if (int.TryParse(slashMatchMetJaar.Groups[1].Value, out var dag) &&
                int.TryParse(slashMatchMetJaar.Groups[2].Value, out var maand) &&
                int.TryParse(slashMatchMetJaar.Groups[3].Value, out var jaar))
            {
                try { return new DateOnly(jaar, maand, dag); } catch { }
            }
        }

        var maandNamen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["januari"] = 1, ["februari"] = 2, ["maart"] = 3, ["april"] = 4,
            ["mei"] = 5, ["juni"] = 6, ["juli"] = 7, ["augustus"] = 8,
            ["september"] = 9, ["oktober"] = 10, ["november"] = 11, ["december"] = 12,
            ["jan"] = 1, ["feb"] = 2, ["mrt"] = 3, ["apr"] = 4,
            ["jun"] = 6, ["jul"] = 7, ["aug"] = 8,
            ["sep"] = 9, ["sept"] = 9, ["okt"] = 10, ["nov"] = 11, ["dec"] = 12
        };

        var tekstLower = tekst.ToLowerInvariant();
        foreach (var (naam, maandNr) in maandNamen)
        {
            var maandMatch = System.Text.RegularExpressions.Regex.Match(tekstLower, $@"(\d{{1,2}})\s+{naam}\b\.?(?:\s+(\d{{4}}))?");
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

    /// <summary>
    /// Club-heuristiek-resolver voor niet-SQL "eigen team"-herkenning (#677/#889). Bewust geen
    /// <c>PostgresClubScope.Resolve</c>: die gooit zonder primaire club, terwijl dit pad juist een
    /// lege uitkomst gracieus moet afhandelen (zie <see cref="BepaalEigenTeamEnTegenstanderAsync"/>).
    /// </summary>
    private static string ResolveHeuristicClubCode(string? clubCodeOverride)
        => !string.IsNullOrWhiteSpace(clubCodeOverride) ? clubCodeOverride : (PostgresAppSettings.GetSetting("clubCode") ?? "");

    /// <summary>
    /// Bepaalt welke van de twee genoemde teams het eigen team is, en zet <c>TeamNaam</c> op de
    /// canonieke naam uit de teamlijst (#700/#889).
    /// </summary>
    private static async Task<bool> BepaalEigenTeamEnTegenstanderAsync(
        BerichtClassificatie classificatie, ITeamResolver resolver, string clubCode, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(clubCode))
        {
            log.LogWarning("TEAMRESOLUTIE - geen clubCode beschikbaar; teamherkenning overgeslagen");
            return false;
        }

        var team = (classificatie.TeamNaam ?? "").Trim();
        var tegenstander = (classificatie.Tegenstander ?? "").Trim();

        var teamUitkomst = await ProbeerResolveAsync(resolver, team, clubCode, log);

        if (teamUitkomst is not null && teamUitkomst.IsOpgelost)
        {
            classificatie.TeamNaam = teamUitkomst.CanoniekeTeamnaam;
            LogUitkomst(log, teamUitkomst);
            return true;
        }

        var tegenstanderUitkomst = await ProbeerResolveAsync(resolver, tegenstander, clubCode, log);
        if (tegenstanderUitkomst is not null && tegenstanderUitkomst.IsOpgelost)
        {
            classificatie.TeamNaam = tegenstanderUitkomst.CanoniekeTeamnaam;
            classificatie.Tegenstander = team;
            log.LogInformation("TEAMRESOLUTIE - team en tegenstander verwisseld op basis van de teamlijst");
            LogUitkomst(log, tegenstanderUitkomst);
            return true;
        }

        var kandidaten = teamUitkomst?.Kandidaten.Count ?? 0;
        log.LogInformation(
            "TEAMRESOLUTIE - geen eigen team herkend (bron={Bron}, kandidaten={Kandidaten})",
            teamUitkomst?.Bron ?? ResolutionBron.Onopgelost, kandidaten);
        return false;
    }

    private static async Task<TeamResolutionResult?> ProbeerResolveAsync(
        ITeamResolver resolver, string ruweTekst, string clubCode, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(ruweTekst)) return null;

        try
        {
            return await resolver.ResolveAsync(new TeamResolutionRequest(ruweTekst, null, null, clubCode));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TEAMRESOLUTIE - resolutie mislukt");
            return null;
        }
    }

    private static void LogUitkomst(ILogger log, TeamResolutionResult uitkomst)
        => log.LogInformation(
            "TEAMRESOLUTIE - teamId={TeamId} bron={Bron} confidence={Confidence}",
            uitkomst.TeamId, uitkomst.Bron, uitkomst.Confidence);

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
