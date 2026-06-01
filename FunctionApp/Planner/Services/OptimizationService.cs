using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner;

/// <summary>
/// Use-case service voor dagplanning-optimalisatie.
/// Extracted uit PlannerService (#475).
/// </summary>
internal static class OptimizationService
{
    public static async Task<OptimaliseerResponse> OptimaliseerAsync(
        OptimaliseerRequest request, string? clubCode, ILogger log)
    {
        var nl = PlannerShared.NL;
        var response = new OptimaliseerResponse { Datum = request.Datum };

        if (!DateOnly.TryParse(request.Datum, out var date))
        {
            response.HtmlPlanner = "<p>Ongeldige datum</p>";
            return response;
        }

        var occupations    = await SportlinkApiClient.GetFieldOccupationsWithApiAsync(date, log);
        var velden         = await PlannerDataAccess.GetVeldenAsync(clubCode);
        var availableFields = await PlannerDataAccess.GetAvailableFieldsAsync(date, clubCode);
        var grasveldNummers  = velden.Where(v => v.VeldType != "kunstgras").Select(v => v.VeldNummer).ToHashSet();
        var kunstgrasNummers = velden.Where(v => v.VeldType == "kunstgras").Select(v => v.VeldNummer).ToHashSet();

        var bezettingen = occupations
            .GroupBy(o => $"{o.VeldNummer}_{o.AanvangsTijd:HH:mm}_{o.Wedstrijd?.Trim()}")
            .Select(g => g.First())
            .OrderBy(o => o.AanvangsTijd).ThenBy(o => o.VeldNummer)
            .ToList();

        if (bezettingen.Count == 0)
        {
            response.VoldoendeRuimte = true;
            response.VoldoendeRuimteMelding = $"Geen wedstrijden gepland op {date.ToString("dddd d MMMM", nl)}.";
            response.HtmlPlanner = $"<p style='color:#8b949e;font-family:sans-serif;'>Geen wedstrijden gepland op {date.ToString("dddd d MMMM yyyy", nl)}.</p>";
            return response;
        }

        var laastste = bezettingen.OrderByDescending(o => o.EindTijd).FirstOrDefault();
        response.HuidigeEindtijd = laastste?.EindTijd.ToString("HH:mm") ?? "—";

        var allTeamRules = new Dictionary<string, List<TeamRegel>>();
        foreach (var teamNaam in bezettingen.Where(o => !string.IsNullOrEmpty(o.TeamNaam)).Select(o => o.TeamNaam!).Distinct())
            allTeamRules[teamNaam] = await PlannerDataAccess.GetTeamRulesAsync(teamNaam);

        var vasteWedstrijden = new HashSet<string>();
        foreach (var b in bezettingen)
            if (b.TeamNaam != null && allTeamRules.TryGetValue(b.TeamNaam, out var regels) && regels.Count > 0)
                vasteWedstrijden.Add($"{b.VeldNummer}_{b.AanvangsTijd:HH:mm}_{b.Wedstrijd?.Trim()}");

        int bufferMin = request.BufferMinuten ?? PlannerShared.StandardBufferMinutes;
        var capaciteit = BerekenCapaciteit(bezettingen, availableFields, grasveldNummers);
        response.CapaciteitOverzicht = capaciteit;

        var doel = (request.Doel ?? "").ToLowerInvariant().Trim();
        bool alleenGrasveld = availableFields.Count > 0 && !availableFields.Any(f => kunstgrasNummers.Contains(f.VeldNummer));
        bool gewensteEindtijdOpgegeven = !string.IsNullOrEmpty(request.GewensteEindtijd);
        bool grasveldOntlastenZinvol   = !alleenGrasveld && capaciteit.AantalWedstrijdenOpGrasveld > 0;
        bool strakkerPlannenZinvol     = gewensteEindtijdOpgegeven || doel == "strakker-plannen";

        if (capaciteit.AantalWedstrijdenOpGrasveld == 0 && capaciteit.BezettingsPercentage < PlannerShared.MaxBezettingsPercentageVoorOverslaan)
        {
            response.VoldoendeRuimte = true;
            response.VoldoendeRuimteMelding =
                $"Voldoende ruimte op {date.ToString("dddd d MMMM", nl)}: " +
                $"geen wedstrijden op grasveld, {capaciteit.BezettingsPercentage:F0}% bezet " +
                $"({capaciteit.AantalLegeVelden} veld(en) ongebruikt). Geen optimalisatie nodig.";
            response.HtmlPlanner = PlannerHtmlGenerator.GenereerHtml(date, bezettingen, new List<OptimalisatieSuggestie>(), velden, doel);
            return response;
        }

        if (!grasveldOntlastenZinvol && !strakkerPlannenZinvol)
        {
            string redenDetail = alleenGrasveld
                ? "doordeweekse dag — alleen grasveld beschikbaar, kunstgrasvelden niet beschikbaar, en er is geen gewenste eindtijd opgegeven"
                : "geen wedstrijden op grasveld en geen gewenste eindtijd opgegeven";
            response.VoldoendeRuimte = true;
            response.VoldoendeRuimteMelding = $"Geen optimalisatie nodig op {date.ToString("dddd d MMMM", nl)}: {redenDetail}.";
            response.HtmlPlanner = PlannerHtmlGenerator.GenereerHtml(date, bezettingen, new List<OptimalisatieSuggestie>(), velden, doel);
            return response;
        }

        var suggesties = new List<OptimalisatieSuggestie>();
        switch (doel)
        {
            case "grasveld-ontlasten":
                if (grasveldOntlastenZinvol)
                    suggesties = OptimaliseerGrasveldOntlasten(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules, bufferMin);
                break;
            case "strakker-plannen":
                if (strakkerPlannenZinvol)
                    suggesties = OptimaliseerStrakkerPlannen(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules, bufferMin);
                break;
            default:
                if (grasveldOntlastenZinvol)
                    suggesties = OptimaliseerGrasveldOntlasten(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules, bufferMin);
                if (strakkerPlannenZinvol)
                {
                    var extra = OptimaliseerStrakkerPlannen(bezettingen, velden, availableFields, vasteWedstrijden, allTeamRules, bufferMin);
                    foreach (var s in extra)
                        if (!suggesties.Any(b => b.Wedstrijd == s.Wedstrijd))
                            suggesties.Add(s);
                }
                break;
        }

        TimeOnly gewensteEindtijd = new(16, 15);
        if (!string.IsNullOrEmpty(request.GewensteEindtijd) && TimeOnly.TryParse(request.GewensteEindtijd, out var parsed))
            gewensteEindtijd = parsed;

        suggesties = VerdeelExtraBuffer(suggesties, bezettingen, availableFields, velden, vasteWedstrijden, allTeamRules, gewensteEindtijd);

        if (suggesties.Count > 0 && bezettingen.Count > 0)
        {
            var huidigeMax = bezettingen.Max(b => b.EindTijd);
            var nieuweMax  = bezettingen.Select(b =>
            {
                var sug = suggesties.FirstOrDefault(s => s.Wedstrijd == b.Wedstrijd?.Trim());
                if (sug != null && TimeOnly.TryParse(sug.NieuweTijd, out var ns))
                    return TimeOnly.FromTimeSpan(ns.ToTimeSpan() + (b.EindTijd.ToTimeSpan() - b.AanvangsTijd.ToTimeSpan()));
                return b.EindTijd;
            }).Max();

            if (nieuweMax >= huidigeMax)
            {
                response.VoldoendeRuimte = true;
                response.VoldoendeRuimteMelding =
                    $"Geen optimalisatie nodig op {date.ToString("dddd d MMMM", nl)}: " +
                    "de huidige planning is al optimaal — verplaatsingen leveren geen eerdere eindtijd op.";
                response.HtmlPlanner = PlannerHtmlGenerator.GenereerHtml(date, bezettingen, new List<OptimalisatieSuggestie>(), velden, doel);
                return response;
            }
        }

        response.Suggesties = suggesties;
        response.AantalVerplaatsingen = suggesties.Count;
        response.AantalVanGrasveldVerplaatst = suggesties.Count(s => grasveldNummers.Contains(s.HuidigVeldNummer));
        response.HtmlPlanner = PlannerHtmlGenerator.GenereerHtml(date, bezettingen, suggesties, velden, request.Doel ?? "optimaliseren");
        return response;
    }

    // ── Privé helpers ──

    private static VeldCapaciteitInfo BerekenCapaciteit(
        List<BestaandeWedstrijd> bezettingen, List<VeldBeschikbaarheidInfo> beschikbareVelden, HashSet<int> grasveldNummers)
    {
        int totaalBeschikbaar = 0;
        foreach (var veld in beschikbareVelden)
            totaalBeschikbaar += (int)(veld.BeschikbaarTot.ToTimeSpan() - veld.BeschikbaarVanaf.ToTimeSpan()).TotalMinutes;
        int totaalBezet = 0;
        foreach (var b in bezettingen)
            totaalBezet += (int)((b.EindTijd.ToTimeSpan() - b.AanvangsTijd.ToTimeSpan()).TotalMinutes * (double)b.VeldDeelGebruik);
        var bezetteVelden = bezettingen.Select(b => b.VeldNummer).Distinct().ToHashSet();
        return new VeldCapaciteitInfo
        {
            TotaalBeschikbareMinuten = totaalBeschikbaar,
            TotaalBezettMinuten = totaalBezet,
            BezettingsPercentage = totaalBeschikbaar > 0 ? (double)totaalBezet / totaalBeschikbaar * 100.0 : 0,
            AantalWedstrijdenOpGrasveld = bezettingen.Count(b => grasveldNummers.Contains(b.VeldNummer)),
            AantalLegeVelden = beschikbareVelden.Count(v => !bezetteVelden.Contains(v.VeldNummer))
        };
    }

    private static List<OptimalisatieSuggestie> OptimaliseerGrasveldOntlasten(
        List<BestaandeWedstrijd> bezettingen, List<VeldInfo> velden,
        List<VeldBeschikbaarheidInfo> beschikbareVelden, HashSet<string> vasteWedstrijden,
        Dictionary<string, List<TeamRegel>> allTeamRules, int bufferMin = 15)
    {
        var grasveldNrs  = velden.Where(v => v.VeldType != "kunstgras").Select(v => v.VeldNummer).ToHashSet();
        var kunstgrasNrs = velden.Where(v => v.VeldType == "kunstgras").Select(v => v.VeldNummer).ToHashSet();
        var suggesties   = new List<OptimalisatieSuggestie>();
        var grasveldWedstrijden = bezettingen
            .Where(b => grasveldNrs.Contains(b.VeldNummer))
            .Where(b => !vasteWedstrijden.Contains($"{b.VeldNummer}_{b.AanvangsTijd:HH:mm}_{b.Wedstrijd?.Trim()}"))
            .OrderBy(b => b.AanvangsTijd).ToList();
        var werkBezettingen = bezettingen.ToList();

        foreach (var wedstrijd in grasveldWedstrijden)
        {
            int duur    = (int)(wedstrijd.EindTijd - wedstrijd.AanvangsTijd).TotalMinutes;
            decimal fractie = wedstrijd.VeldDeelGebruik;
            var huidigVeldNaam = velden.FirstOrDefault(v => v.VeldNummer == wedstrijd.VeldNummer)?.VeldNaam ?? $"veld {wedstrijd.VeldNummer}";

            CandidateSlot? besteSlot = null;
            foreach (var veldBesch in beschikbareVelden.Where(f => kunstgrasNrs.Contains(f.VeldNummer)).OrderBy(f => f.VeldNummer))
            {
                var veldBezetting = werkBezettingen.Where(b => b.VeldNummer == veldBesch.VeldNummer).ToList();
                for (var tijd = veldBesch.BeschikbaarVanaf; tijd.AddMinutes(duur) <= veldBesch.BeschikbaarTot; tijd = tijd.AddMinutes(5))
                {
                    if (tijd > wedstrijd.AanvangsTijd) break;
                    var eindTijd = tijd.AddMinutes(duur);
                    if (PlannerShared.CanFitMatch(tijd, eindTijd, fractie, veldBesch.VeldNummer,
                                    veldBezetting, allTeamRules, new List<TeamRegel>()))
                    {
                        besteSlot = new CandidateSlot { VeldNummer = veldBesch.VeldNummer, AanvangsTijd = tijd, EindTijd = eindTijd };
                        break;
                    }
                }
                if (besteSlot != null) break;
            }

            if (besteSlot != null)
            {
                var veldNaam = velden.FirstOrDefault(v => v.VeldNummer == besteSlot.VeldNummer)?.VeldNaam ?? $"veld {besteSlot.VeldNummer}";
                suggesties.Add(new OptimalisatieSuggestie
                {
                    Wedstrijd = wedstrijd.Wedstrijd?.Trim() ?? "",
                    HuidigVeldNummer = wedstrijd.VeldNummer, HuidigVeld = huidigVeldNaam,
                    HuidigeTijd = wedstrijd.AanvangsTijd.ToString("HH:mm"),
                    NieuwVeldNummer = besteSlot.VeldNummer, NieuwVeld = veldNaam,
                    NieuweTijd = PlannerShared.RondAfOp5Min(besteSlot.AanvangsTijd).ToString("HH:mm"),
                    Reden = $"Verplaats van {huidigVeldNaam} (grasveld) naar {veldNaam} (kunstgras)"
                });
                werkBezettingen.Remove(wedstrijd);
                var afgerond = PlannerShared.RondAfOp5Min(besteSlot.AanvangsTijd);
                werkBezettingen.Add(new BestaandeWedstrijd
                {
                    Datum = wedstrijd.Datum, AanvangsTijd = afgerond, EindTijd = afgerond.AddMinutes(duur),
                    VeldNummer = besteSlot.VeldNummer, VeldDeelGebruik = fractie,
                    TeamNaam = wedstrijd.TeamNaam, Wedstrijd = wedstrijd.Wedstrijd, Bron = "Suggestie"
                });
            }
        }
        return suggesties;
    }

    private static List<OptimalisatieSuggestie> OptimaliseerStrakkerPlannen(
        List<BestaandeWedstrijd> bezettingen, List<VeldInfo> velden,
        List<VeldBeschikbaarheidInfo> beschikbareVelden, HashSet<string> vasteWedstrijden,
        Dictionary<string, List<TeamRegel>> allTeamRules, int bufferMin = 15)
    {
        var suggesties = new List<OptimalisatieSuggestie>();
        var werkBezetting = bezettingen.ToList();
        var blokken = bezettingen
            .GroupBy(b => $"{b.VeldNummer}_{b.AanvangsTijd:HH:mm}")
            .Select(g => new
            {
                VeldNummer = g.First().VeldNummer, AanvangsTijd = g.First().AanvangsTijd,
                EindTijd = g.Max(w => w.EindTijd),
                Wedstrijden = g.GroupBy(w => w.Wedstrijd?.Trim()).Select(wg => wg.First()).ToList(),
                IsVast = g.Any(w => vasteWedstrijden.Contains($"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}"))
            })
            .Where(b => !b.IsVast)
            .OrderBy(b => b.AanvangsTijd).ThenBy(b => b.VeldNummer).ToList();

        foreach (var blok in blokken)
        {
            int duur = (int)(blok.EindTijd - blok.AanvangsTijd).TotalMinutes;
            decimal blokFractie = blok.Wedstrijden.Sum(w => w.VeldDeelGebruik);
            var wedstrijd = blok.Wedstrijden.First();
            bool isDeelveld = blokFractie > 0 && blokFractie <= 1.0m && blok.Wedstrijden.Count > 1;
            var teChecken = isDeelveld
                ? beschikbareVelden.Where(v => v.VeldNummer == blok.VeldNummer).ToList()
                : beschikbareVelden.OrderBy(v => v.VeldNummer).ToList();

            TimeOnly? besteSlotTijd = null;
            int besteSlotVeld = blok.VeldNummer;

            foreach (var kv in teChecken)
            {
                var kandidaatBezetting = werkBezetting
                    .Where(b => b.VeldNummer == kv.VeldNummer)
                    .Where(b => !blok.Wedstrijden.Any(bw => bw.VeldNummer == b.VeldNummer && bw.AanvangsTijd == b.AanvangsTijd && bw.Wedstrijd?.Trim() == b.Wedstrijd?.Trim()))
                    .ToList();
                for (var tijd = kv.BeschikbaarVanaf; tijd.AddMinutes(duur) <= kv.BeschikbaarTot; tijd = tijd.AddMinutes(5))
                {
                    if (tijd >= blok.AanvangsTijd) break;
                    var eindTijd = tijd.AddMinutes(duur);
                    bool past = isDeelveld
                        ? blok.Wedstrijden.All(bw => PlannerShared.CanFitMatch(tijd, eindTijd, bw.VeldDeelGebruik, kv.VeldNummer, kandidaatBezetting, allTeamRules, new List<TeamRegel>()))
                        : PlannerShared.CanFitMatch(tijd, eindTijd, 1.0m, kv.VeldNummer, kandidaatBezetting, allTeamRules, new List<TeamRegel>());
                    if (past) { if (besteSlotTijd == null || tijd < besteSlotTijd.Value) { besteSlotTijd = tijd; besteSlotVeld = kv.VeldNummer; } break; }
                }
            }

            if (besteSlotTijd.HasValue)
            {
                var verschil = (blok.AanvangsTijd - besteSlotTijd.Value).TotalMinutes;
                if (verschil >= 15)
                {
                    var huidigVeldNaam = velden.FirstOrDefault(v => v.VeldNummer == blok.VeldNummer)?.VeldNaam ?? $"veld {blok.VeldNummer}";
                    var nieuwVeldNaam  = velden.FirstOrDefault(v => v.VeldNummer == besteSlotVeld)?.VeldNaam ?? $"veld {besteSlotVeld}";
                    var reden = besteSlotVeld == blok.VeldNummer
                        ? $"Naar voren schuiven ({(int)verschil} min eerder)"
                        : $"Verplaatsen naar {nieuwVeldNaam} ({(int)verschil} min eerder)";
                    foreach (var bw in blok.Wedstrijden)
                        suggesties.Add(new OptimalisatieSuggestie
                        {
                            Wedstrijd = bw.Wedstrijd?.Trim() ?? "", HuidigVeldNummer = bw.VeldNummer,
                            HuidigVeld = huidigVeldNaam, HuidigeTijd = bw.AanvangsTijd.ToString("HH:mm"),
                            NieuwVeldNummer = besteSlotVeld, NieuwVeld = nieuwVeldNaam,
                            NieuweTijd = PlannerShared.RondAfOp5Min(besteSlotTijd.Value).ToString("HH:mm"), Reden = reden
                        });
                    foreach (var bw in blok.Wedstrijden)
                        werkBezetting.RemoveAll(b => b.VeldNummer == bw.VeldNummer && b.AanvangsTijd == bw.AanvangsTijd && b.Wedstrijd?.Trim() == bw.Wedstrijd?.Trim());
                    var afgerond = PlannerShared.RondAfOp5Min(besteSlotTijd.Value);
                    foreach (var bw in blok.Wedstrijden)
                    {
                        int bwDuur = (int)(bw.EindTijd - bw.AanvangsTijd).TotalMinutes;
                        werkBezetting.Add(new BestaandeWedstrijd
                        {
                            Datum = bw.Datum, AanvangsTijd = afgerond, EindTijd = afgerond.AddMinutes(bwDuur),
                            VeldNummer = besteSlotVeld, VeldDeelGebruik = bw.VeldDeelGebruik,
                            TeamNaam = bw.TeamNaam, Wedstrijd = bw.Wedstrijd, Bron = "Suggestie"
                        });
                    }
                }
            }
        }
        return suggesties;
    }

    private static List<OptimalisatieSuggestie> VerdeelExtraBuffer(
        List<OptimalisatieSuggestie> suggesties, List<BestaandeWedstrijd> origineleBezetting,
        List<VeldBeschikbaarheidInfo> beschikbareVelden, List<VeldInfo> velden,
        HashSet<string> vasteWedstrijden, Dictionary<string, List<TeamRegel>> allTeamRules,
        TimeOnly gewensteEindtijd)
    {
        const int maxBuffer = 30;
        var nieuweBezetting = origineleBezetting.GroupBy(w => $"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}").Select(g => g.First()).ToList();
        foreach (var s in suggesties)
        {
            nieuweBezetting.RemoveAll(b => b.VeldNummer == s.HuidigVeldNummer && b.AanvangsTijd.ToString("HH:mm") == s.HuidigeTijd && b.Wedstrijd?.Trim() == s.Wedstrijd.Trim());
            TimeOnly.TryParse(s.NieuweTijd, out var nt);
            var orig = origineleBezetting.FirstOrDefault(b => b.VeldNummer == s.HuidigVeldNummer && b.AanvangsTijd.ToString("HH:mm") == s.HuidigeTijd && b.Wedstrijd?.Trim() == s.Wedstrijd.Trim());
            int duur = orig != null ? (int)(orig.EindTijd - orig.AanvangsTijd).TotalMinutes : 75;
            nieuweBezetting.Add(new BestaandeWedstrijd
            {
                Datum = orig?.Datum ?? default, AanvangsTijd = nt, EindTijd = nt.AddMinutes(duur),
                VeldNummer = s.NieuwVeldNummer, VeldDeelGebruik = orig?.VeldDeelGebruik ?? 1.0m,
                TeamNaam = orig?.TeamNaam, Wedstrijd = orig?.Wedstrijd, Bron = "Suggestie"
            });
        }

        foreach (var veldBesch in beschikbareVelden)
        {
            var veldW = nieuweBezetting.Where(b => b.VeldNummer == veldBesch.VeldNummer && b.VeldDeelGebruik >= 1.0m).OrderBy(b => b.AanvangsTijd).ToList();
            if (veldW.Count < 2) continue;
            var laaststeEinde = veldW.Max(w => w.EindTijd);
            if (laaststeEinde >= gewensteEindtijd) continue;
            int aantalGaten = 0;
            for (int i = 1; i < veldW.Count; i++)
            {
                string key = $"{veldW[i].VeldNummer}_{veldW[i].AanvangsTijd:HH:mm}_{veldW[i].Wedstrijd?.Trim()}";
                if (!vasteWedstrijden.Contains(key)) aantalGaten++;
            }
            if (aantalGaten == 0) continue;
            double extraMin = (gewensteEindtijd - laaststeEinde).TotalMinutes;
            int extraPerGat = Math.Min((int)(extraMin / aantalGaten), maxBuffer - PlannerShared.StandardBufferMinutes);
            if (extraPerGat <= 0) continue;
            int cumulatief = 0;
            for (int i = 0; i < veldW.Count; i++)
            {
                if (i > 0)
                {
                    string key = $"{veldW[i].VeldNummer}_{veldW[i].AanvangsTijd:HH:mm}_{veldW[i].Wedstrijd?.Trim()}";
                    if (!vasteWedstrijden.Contains(key)) cumulatief += extraPerGat;
                }
                if (cumulatief > 0)
                {
                    var w = veldW[i];
                    var nieuweTijd = w.AanvangsTijd.AddMinutes(cumulatief);
                    var bestaand = suggesties.FirstOrDefault(s => s.Wedstrijd.Trim() == w.Wedstrijd?.Trim() && s.NieuwVeldNummer == w.VeldNummer);
                    if (bestaand != null)
                    {
                        bestaand.NieuweTijd = PlannerShared.RondAfOp5Min(nieuweTijd).ToString("HH:mm");
                    }
                    else
                    {
                        var origKey = $"{w.VeldNummer}_{w.AanvangsTijd:HH:mm}_{w.Wedstrijd?.Trim()}";
                        if (!vasteWedstrijden.Contains(origKey))
                        {
                            var veldNaam = velden.FirstOrDefault(v => v.VeldNummer == w.VeldNummer)?.VeldNaam ?? $"veld {w.VeldNummer}";
                            suggesties.Add(new OptimalisatieSuggestie
                            {
                                Wedstrijd = w.Wedstrijd?.Trim() ?? "", HuidigVeldNummer = w.VeldNummer,
                                HuidigVeld = veldNaam, HuidigeTijd = w.AanvangsTijd.ToString("HH:mm"),
                                NieuwVeldNummer = w.VeldNummer, NieuwVeld = veldNaam,
                                NieuweTijd = PlannerShared.RondAfOp5Min(nieuweTijd).ToString("HH:mm"),
                                Reden = $"Extra buffer (+{cumulatief} min)"
                            });
                        }
                    }
                }
            }
        }
        return suggesties;
    }
}
