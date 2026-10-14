using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared.TeamResolution;
using Planner.Shared;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Planner.Repositories;
using FunctionApp.Postgres.Sync;

namespace FunctionApp.Postgres.Processing;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Processing/BerichtPipeline.cs</c> (#889). Kanaal-
/// agnostische verwerkingspipeline voor inkomende berichten — geoefend door zowel het dry-run pad
/// (<c>EmailTestFunction</c>) als, sinds de #972-hotfix (#1044), de echte mailbox-getriggerde
/// <c>EmailProcessorFunction</c>.
///
/// <para>
/// Drie eerder gedocumenteerde afwijkingen ten opzichte van het SQL Server-origineel zijn
/// inmiddels vertaald: het "opponent kan ons team alsnog vinden"-pad sinds #1139
/// (<see cref="PlannerMatchRepository.FindMatchByOpponentAsync"/>, zie de
/// <c>BeschikbaarheidCheck</c>-tak hieronder), <c>TeamContactOpvragen</c>/<c>coachGevonden</c>
/// sinds #1140 (<see cref="AllstarsTestDataRepository.GetTeamleiderContactAsync"/>, zie die tak
/// hieronder), en het "verzet zonder datum"-pad (#561, KNVB-bijlage + vrije-zaterdagen-voorzet)
/// sinds #1141 — zie <see cref="BouwVerzetZonderDatumResponseAsync"/>, <see cref="KnvbKalenderRepository"/>
/// en <see cref="PostgresSeasonHelper.GetCurrentKnvbSeizoenAsync"/>.
/// </para>
/// </summary>
internal static partial class BerichtPipeline
{
    /// <summary>
    /// Corrigeert de datum van de AI-classificatie op basis van onderwerp en body. De regels
    /// (expliciete datum in onderwerp &gt; in de eigen tekst &gt; AI-datum met dagnaamcorrectie) staan sinds
    /// #1716 één keer in <see cref="BerichtDatumHeuristiek.CorrigeerDagDatum"/>.
    /// </summary>
    internal static void ValideerDagDatum(BerichtClassificatie classificatie, string emailBody, string onderwerp)
        => classificatie.Datum = BerichtDatumHeuristiek.CorrigeerDagDatum(classificatie.Datum, emailBody, onderwerp);

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
        string? clubCode = null, ClubAppSettingsSnapshot? clubSettings = null, TraceBuilder? trace = null)
    {
        ArgumentNullException.ThrowIfNull(teamResolver);
        // #1730: de ene strikte grens voor alle modeldatumvelden, vóór trace, teamherkenning en verwerking.
        (classificatie.Datum, classificatie.GewensteDatum, classificatie.Datums) = ModelDatum.Grens(classificatie.Datum, classificatie.GewensteDatum, classificatie.Datums);
        trace?.Classificatie(classificatie.Type.ToString(), classificatie.TeamNaam, classificatie.Tegenstander, classificatie.GetAlleDatums().Count, classificatie.AanvangsTijd);

        var cs = PostgresDatabaseConfig.ConnectionString;
        var cc = ResolveHeuristicClubCode(clubCode);
        var eigenTeamHerkend = await BepaalEigenTeamEnTegenstanderAsync(classificatie, teamResolver, cc, log, trace);
        classificatie.TeamNaamOnbevestigd = !eigenTeamHerkend; // antwoorden noemen een niet-bevestigd team nooit bij naam
        if (eigenTeamHerkend) classificatie.LeeftijdsCategorie = TeamNaamNormalisatie.VulLeeftijdsCategorieAan(classificatie.LeeftijdsCategorie, classificatie.TeamNaam, cc);

        switch (classificatie.Type)
        {
            case VerzoekType.BeschikbaarheidCheck:
                return await VerwerkBeschikbaarheidAsync(
                    cs, classificatie, bericht, log, teamResolver, cc, eigenTeamHerkend, clubCode, trace);

            case VerzoekType.HerplanVerzoek:
                return await VerwerkHerplanAsync(cs, classificatie, bericht, log, cc, clubCode, clubSettings);

            case VerzoekType.TeamContactOpvragen:
                return await VerwerkTeamContactAsync(cs, classificatie, clubCode);

            case VerzoekType.Bevestiging:
                return JsonConvert.SerializeObject(new { status = "Bevestiging ontvangen", opmerking = "Bevestigingen vereisen handmatige afhandeling door de coördinator" });

            default:
                return JsonConvert.SerializeObject(new { status = "Niet verwerkt" });
        }
    }

    /// <summary>
    /// Postgres-tier-tegenhanger van <c>FunctionApp/Processing/BerichtPipeline.cs</c>'s gelijknamige
    /// methode (#561/#1141). Bouwt het "verzet zonder datum"-pad voor een
    /// <see cref="VerzoekType.HerplanVerzoek"/> van de tegenstander zonder concrete
    /// <c>GewensteDatum</c>. Een tegenstander mag geen nieuwe datum toegezegd krijgen — die
    /// afstemming hoort bij de begeleiding van ons eigen team. In plaats daarvan geeft het antwoord
    /// een paar concrete "vrije zaterdagen" als voorzet, en de pipeline markeert de classificatie
    /// zodat de KNVB-kalender-PDF als bijlage meegaat en de begeleiding van ons team in BCC komt
    /// (zie <see cref="Email.EmailReplyPolicyService"/>).
    ///
    /// Retourneert <c>null</c> als <c>knvbStandaardRegio</c> ontbreekt, de PDF-bijlage-instelling
    /// uit staat, of er geen (toekomstig) seizoen in <c>public.clubseason</c> staat — de aanroepende tak
    /// valt dan terug op het bestaande gedrag (<see cref="RescheduleService.CheckRescheduleAvailabilityAsync"/>).
    /// Nooit een regio gokken of hardcoden: ontbreekt de instelling, dan is er geen bijlage/flow.
    /// </summary>
    private static async Task<string?> BouwVerzetZonderDatumResponseAsync(
        string connectionString, BerichtClassificatie classificatie, ZoekWedstrijdResponse wedstrijd,
        int deadlineDagen, ClubAppSettingsSnapshot? clubSettings, string clubCode, ILogger log)
    {
        var regio = clubSettings != null
            ? clubSettings.KnvbStandaardRegio
            : PostgresAppSettings.GetSetting("knvbStandaardRegio");
        if (string.IsNullOrWhiteSpace(regio))
        {
            log.LogInformation("VERZET-ZONDER-DATUM - geen knvbStandaardRegio ingesteld; val terug op het standaard herplan-pad");
            return null;
        }

        bool bijlageAan;
        if (clubSettings != null)
        {
            bijlageAan = clubSettings.KnvbPdfBijlageIngeschakeld ?? false;
        }
        else
        {
            var raw = PostgresAppSettings.GetSetting("knvbPdfBijlageIngeschakeld");
            bijlageAan = !(string.IsNullOrWhiteSpace(raw)
                || raw == "0"
                || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase));
        }
        if (!bijlageAan)
        {
            log.LogInformation("VERZET-ZONDER-DATUM - KNVB-PDF-bijlage staat uit; val terug op het standaard herplan-pad");
            return null;
        }

        var seizoen = await PostgresSeasonHelper.GetCurrentKnvbSeizoenAsync(log, clubCode);
        if (string.IsNullOrWhiteSpace(seizoen))
        {
            log.LogInformation("VERZET-ZONDER-DATUM - geen (toekomstig) seizoen gevonden in public.clubseason; val terug op het standaard herplan-pad");
            return null;
        }

        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        var van = vandaag.AddDays(deadlineDagen);
        // 8 weken venster — een algoritmische constante (geen club-specifieke waarde), geeft
        // voldoende keuze zonder een half seizoen aan zaterdagen op te sommen.
        var tot = van.AddDays(56);

        var reedsBezetteData = new HashSet<DateOnly>();
        if (!string.IsNullOrWhiteSpace(classificatie.TeamNaam))
        {
            var toekomstigeWedstrijden = await PlannerMatchRepository.GetFutureMatchesForTeamAsync(
                connectionString, classificatie.TeamNaam, van, tot, clubCode);
            foreach (var m in toekomstigeWedstrijden)
            {
                if (DateOnly.TryParse(m.Datum, out var bezetteDatum))
                    reedsBezetteData.Add(bezetteDatum);
            }
        }

        var vrijeZaterdagen = await KnvbKalenderRepository.GetVrijeZaterdagenAsync(
            connectionString, regio, seizoen, van, tot, reedsBezetteData, maxAantal: 5, clubCode);

        classificatie.VoegKnvbPdfBijlageToe = true;
        classificatie.KnvbBijlageRegio = regio;

        return JsonConvert.SerializeObject(new
        {
            verzetZonderDatum = true,
            wedstrijd,
            vrijeZaterdagen = vrijeZaterdagen.Select(d => d.ToString("yyyy-MM-dd")).ToList(),
            regio,
            seizoen
        });
    }

    /// <summary>
    /// Bouwt het antwoord via templates op basis van het classificatietype en response.
    /// Probeert eerst een DB-override op te halen via EmailTemplateService (#287).
    /// Valt terug op hardcoded defaults als er geen override is.
    /// </summary>
    internal static Task<(string onderwerp, string body)> BouwTemplateAntwoord(
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        InkomendBericht bericht,
        ILogger? log = null,
        ClubAppSettingsSnapshot? clubSettings = null,
        string? clubCode = null,
        TraceBuilder? trace = null,
        Func<string, Task<EmailTemplate?>>? sjabloonBron = null)
    {
        // #1627: de takkeuze staat in Planner.Shared (TemplateAntwoordBouwer); deze tier levert alleen
        // de vertaling naar BerichtResponseGenerator/DTO's (TemplateAntwoordBron) en de sjabloonopslag.
        sjabloonBron ??= sleutel => Email.EmailTemplateService.GetTemplateAsync(sleutel, clubCode, log);
        var bron = new TemplateAntwoordBron(classificatie, bericht, clubSettings, trace, sjabloonBron);
        return TemplateAntwoordBouwer.BouwAsync(classificatie.Type.ToString(), plannerResponseJson, bron, trace);
    }

    // ── Private helpers ──

    /// <summary>
    /// Het plannerverzoek voor één datum van een beschikbaarheidscheck. Geeft het door de afzender genoemde
    /// dagdeel door (#1587), zodat de planner vensters en alternatieven binnen dat dagdeel zoekt in plaats
    /// van het standaardvenster van de hele dag.
    /// </summary>
    internal static CheckAvailabilityRequest BouwBeschikbaarheidRequest(BerichtClassificatie classificatie, string datum)
        => new()
        {
            Datum = datum,
            AanvangsTijd = classificatie.AanvangsTijd,
            Dagdeel = classificatie.Dagdeel,
            LeeftijdsCategorie = classificatie.LeeftijdsCategorie,
            TeamNaam = classificatie.TeamNaam,
            Tegenstander = classificatie.Tegenstander,
            HeelVeld = classificatie.HeelVeld
        };

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
        BerichtClassificatie classificatie, ITeamResolver resolver, string clubCode, ILogger log, TraceBuilder? trace)
    {
        if (string.IsNullOrWhiteSpace(clubCode))
        {
            trace?.TeamHerkenningOvergeslagen("geen clubcode");
            log.LogWarning("TEAMRESOLUTIE - geen clubCode beschikbaar; teamherkenning overgeslagen");
            return false;
        }

        var team = (classificatie.TeamNaam ?? "").Trim();
        var tegenstander = (classificatie.Tegenstander ?? "").Trim();

        var teamUitkomst = await ProbeerResolveAsync(resolver, team, clubCode, log, trace, TraceCodes.TeamHerkenning);

        if (teamUitkomst is not null && teamUitkomst.IsOpgelost)
        {
            classificatie.TeamNaam = teamUitkomst.CanoniekeTeamnaam;
            LogUitkomst(log, teamUitkomst);
            return true;
        }

        var tegenstanderUitkomst = await ProbeerResolveAsync(resolver, tegenstander, clubCode, log, trace, TraceCodes.TegenstanderHerkenning);
        if (tegenstanderUitkomst is not null && tegenstanderUitkomst.IsOpgelost)
        {
            classificatie.TeamNaam = tegenstanderUitkomst.CanoniekeTeamnaam;
            classificatie.Tegenstander = team;
            trace?.TeamWissel(classificatie.TeamNaam, team);
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

    /// <summary>
    /// Leidt het eigen team af uit de wedstrijdnaam ("TeamA - TeamB") die de opponent-lookup
    /// (#1139) opleverde — Postgres-tegenhanger van het gelijknamige SQL Server-origineel.
    /// Probeert beide kanten van het streepje tegen de teamresolver; de eerste kant die
    /// oplosbaar is naar een bekend team levert de canonieke naam. Geen van beide oplosbaar?
    /// Dan blijft de classificatie ongewijzigd en handelt de aanroepende tak dat af als "team
    /// onbekend". Er wordt nooit een naam gegokt.
    /// </summary>
    private static async Task<string?> BepaalEigenTeamUitWedstrijdAsync(
        string? wedstrijd, ITeamResolver resolver, string clubCode, ILogger log, TraceBuilder? trace)
    {
        if (string.IsNullOrWhiteSpace(wedstrijd)) return null;

        foreach (var kant in wedstrijd.Split(" - ", 2, StringSplitOptions.TrimEntries))
        {
            var uitkomst = await ProbeerResolveAsync(resolver, kant, clubCode, log, trace, TraceCodes.OpponentTeamHerkenning);
            if (uitkomst is not null && uitkomst.IsOpgelost)
                return uitkomst.CanoniekeTeamnaam;
        }
        return null;
    }

    private static async Task<TeamResolutionResult?> ProbeerResolveAsync(
        ITeamResolver resolver, string ruweTekst, string clubCode, ILogger log, TraceBuilder? trace, string code)
    {
        if (string.IsNullOrWhiteSpace(ruweTekst)) return null;

        try
        {
            var u = await resolver.ResolveAsync(new TeamResolutionRequest(ruweTekst, null, null, clubCode));
            trace?.TeamHerkenning(code, ruweTekst, u.Bron.ToString(), u.Confidence, u.Kandidaten.Select(k => k.Teamnaam), u.IsOpgelost ? u.CanoniekeTeamnaam : null);
            return u;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TEAMRESOLUTIE - resolutie mislukt");
            trace?.TeamHerkenning(code, ruweTekst, null, 0, null, null);
            return null;
        }
    }

    private static void LogUitkomst(ILogger log, TeamResolutionResult uitkomst)
        => log.LogInformation(
            "TEAMRESOLUTIE - teamId={TeamId} bron={Bron} confidence={Confidence}",
            uitkomst.TeamId, uitkomst.Bron, uitkomst.Confidence);
}
