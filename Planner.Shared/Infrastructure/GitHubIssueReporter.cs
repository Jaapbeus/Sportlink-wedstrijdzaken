using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Planner.Shared.Infrastructure;

/// <summary>
/// Rapporteert onverwachte exceptions als GitHub Issues.
/// Deduplicatie op fingerprint (zie <see cref="ComputeFingerprint"/>):
///   - Bestaand open issue → voeg comment toe
///   - Bestaand gesloten issue → heropen + voeg comment toe (de fout is opnieuw opgetreden)
///   - Geen bestaand issue → maak nieuw issue aan met de conventielabels (<see cref="NieuwIssueLabels"/>)
///     en plaats daarna de vaste opdrachtregel (<see cref="OpdrachtRegel"/>, #1711)
///   - Al gerapporteerd binnen 24u → overslaan (rate-limiting, issue #106)
///
/// <para>
/// <b>Waarom dit in Planner.Shared staat (#1268).</b> Deze klasse doet een HTTP-aanroep naar de
/// GitHub API, dedupliceert op een fingerprint en bouwt een issue-body. Geen enkele stap daarvan
/// is databasetier-specifiek: de configuratie komt volledig uit omgevingsvariabelen. Ze stond tot
/// #1268 uitsluitend in de SQL Server-tier, waardoor de Postgres-tier — de tier die in productie
/// draait — runtimefouten alleen als logregel achterliet. Een kopie maken zou precies de fout zijn
/// die <c>ThemeCore</c> (#1248), <c>FeedbackCore</c> (#1130) en <c>SportlinkEndpointCore</c>
/// (#1266) hebben opgeruimd: twee bestanden die niets van elkaar weten, niet gedeeld getest worden
/// en dus stilzwijgend uit elkaar lopen.
/// </para>
///
/// <para>
/// <b>Twee tier-specifieke dingen komen als parameter binnen</b>, zelfde vorm als
/// <c>SportlinkEndpointCore.BepaalTimerStatus(..., Func&lt;bool&gt; egressToegestaan)</c>:
/// <list type="bullet">
///   <item><description><c>egressToegestaan</c> — de <c>EgressGuard</c> van de aanroepende tier
///   (#857). Die guard blijft bewust per tier staan: hij is een poort van díe Function App, en
///   beide tiers gebruiken hem op tientallen plaatsen. Hier meegeven is goedkoper en eerlijker dan
///   een tweede, impliciete "is dit toegestaan?"-controle in gedeelde code.</description></item>
///   <item><description><c>eigenNamespacePrefix</c> — het namespace-voorvoegsel van de eigen code
///   van die tier (<c>SportlinkFunction.</c> respectievelijk <c>FunctionApp.Postgres.</c>).
///   <see cref="GetCallerFrame"/> gebruikt dat om de eerste eigen stackframe te vinden. Eén vaste
///   lijst met beide voorvoegsels zou de fingerprints van de bestaande tier verschuiven en dus de
///   dedup van reeds aangemaakte issues breken.</description></item>
/// </list>
/// </para>
///
/// De dedup-lookup (<see cref="SearchIssueAsync"/>) gebruikt de gewone Issues List API
/// (<c>GET /repos/{owner}/{repo}/issues</c>), niet de GitHub Search API. De Search API bleek
/// bij een fine-grained PAT met alleen <c>issues:write</c>-scope onbetrouwbaar (403/404), wat
/// de dedup stilzwijgend liet terugvallen op "altijd een nieuw issue aanmaken" — zie #830.
///
/// Vereiste environment variables:
///   GitHubPat   — fine-grained PAT met issues:write scope (zie #103)
///   GitHubOwner — GitHub organisatie of gebruikersnaam (default: env GITHUB_REPOSITORY_OWNER)
///   GitHubRepo  — repository naam (verplicht — geen fallback, zie #607)
///
/// Wanneer GitHubPat niet geconfigureerd is, wordt alles stil overgeslagen. De PAT wordt nooit
/// gelogd en komt uitsluitend in de Authorization-header terecht.
/// </summary>
public static class GitHubIssueReporter
{
    private static readonly Dictionary<string, DateTime> _recentlyReported = new();
    private static readonly object _lock = new();

    private const int RateLimitHours = 24;

    /// <summary>Herkomstlabel van een automatisch gemelde exception (#1711), naar het voorbeeld
    /// van <c>via: feedback-widget</c>: herkomst is geen persoon, dus geen <c>source:</c>.</summary>
    internal const string HerkomstLabel = "via: auto-exception";

    /// <summary>Label dat deze reporter tot #1711 zette; oude meldingen zijn er nog op te vinden.</summary>
    internal const string OudLabel = "bug";

    /// <summary>
    /// Labels van een nieuw issue (#1711), volgens de labelconventie in CONTRIBUTING.md: precies
    /// één <c>type:</c>, één <c>priority:</c> en één herkomstlabel. Bewust zonder <c>status:</c>
    /// (de automatiek zet <c>status: triage</c>), zonder het oude <c>bug</c> en zonder
    /// <c>turn: claude-code</c>: dat label start de CI-bot (<c>claude-issue-turn.yml</c>) als
    /// tweede implementer naast de interactieve sessie die de opdracht krijgt (#1692, #1702).
    /// </summary>
    internal static readonly IReadOnlyList<string> NieuwIssueLabels =
        new[] { "type: bug", "priority: medium", HerkomstLabel };

    /// <summary>
    /// Vaste opdrachtregel (eigenaarsbesluit op #1711): als eerste comment geplaatst, met dezelfde
    /// <c>issues:write</c>-rechten als het issue zelf. Vaste tekst zonder runtimewaarden (SECURITY.md).
    /// </summary>
    internal const string OpdrachtRegel =
        "Opdracht vastgelegd (automatische melding, #1711): implementer Claude Code, reviewer Codex, fase triage. "
        + "Verwerking door de eerstvolgende interactieve Claude Code-sessie; die legt bij oppak sessie, branch "
        + "en worktree vast (AGENTS.md). Bewust geen `turn: claude-code`: dat label start de CI-bot als tweede "
        + "implementer (CONTRIBUTING.md, #1692).";

    /// <param name="egressToegestaan">De <c>EgressGuard</c> van de aanroepende tier (#857).</param>
    /// <param name="eigenNamespacePrefix">Namespace-voorvoegsel van de eigen code van die tier,
    /// inclusief de afsluitende punt — bijv. <c>"SportlinkFunction."</c>.</param>
    public static async Task ReportAsync(
        Exception ex,
        string functionName,
        ILogger log,
        Func<bool> egressToegestaan,
        string eigenNamespacePrefix)
    {
        if (!egressToegestaan())
        {
            log.LogInformation("EgressGuard: uitgaande integraties geblokkeerd buiten productie — issue-rapportage overgeslagen (#857).");
            return;
        }

        var pat = Environment.GetEnvironmentVariable("GitHubPat");
        if (string.IsNullOrWhiteSpace(pat))
        {
            log.LogDebug("GitHubPat niet geconfigureerd — exception-reporting naar GitHub overgeslagen");
            return;
        }

        var owner = Environment.GetEnvironmentVariable("GitHubOwner")
                 ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY_OWNER")
                 ?? "";
        // Geen stille fallback op de upstream-repo-naam: een fork met een andere naam zou dan
        // issues naar de verkeerde repo proberen te sturen (404). (#607)
        var repo = Environment.GetEnvironmentVariable("GitHubRepo");

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            log.LogWarning("GitHubOwner/GitHubRepo niet volledig geconfigureerd — issue-reporting overgeslagen");
            return;
        }

        var fp = ComputeFingerprint(ex, eigenNamespacePrefix);

        if (!ProbeerRegistreren(fp, DateTime.UtcNow))
        {
            log.LogInformation("Exception fp:{Fp} al gerapporteerd binnen {H}u — overgeslagen", fp, RateLimitHours);
            return;
        }

        try
        {
            using var http = BuildHttpClient(pat);
            var existing = await SearchIssueAsync(http, owner, repo, fp, log);

            if (existing.HasValue)
            {
                if (existing.Value.isClosed)
                    await ReopenIssueAsync(http, owner, repo, existing.Value.number, log);
                await AddCommentAsync(http, owner, repo, existing.Value.number, ex, functionName, fp, log);
            }
            else
                await CreateIssueAsync(http, owner, repo, fp, ex, functionName, log);
        }
        catch (Exception reportEx)
        {
            log.LogWarning(reportEx, "GitHubIssueReporter: fout bij rapporteren van exception fp:{Fp}", fp);
        }
    }

    /// <summary>
    /// In-memory rate-limiting (#106): registreert <paramref name="fp"/> als "nu gerapporteerd" en
    /// geeft <c>false</c> terug als dezelfde fingerprint binnen <see cref="RateLimitHours"/> uur al
    /// geregistreerd was. Los testbaar gemaakt in #1268 — hiervóór zat deze beslissing als
    /// <c>lock</c>-blok midden in <see cref="ReportAsync"/> en was hij alleen te raken door een
    /// echte GitHub-aanroep te doen.
    /// </summary>
    internal static bool ProbeerRegistreren(string fp, DateTime nuUtc)
    {
        lock (_lock)
        {
            if (_recentlyReported.TryGetValue(fp, out var last)
                && (nuUtc - last).TotalHours < RateLimitHours)
            {
                return false;
            }
            _recentlyReported[fp] = nuUtc;
            return true;
        }
    }

    /// <summary>
    /// Berekent een deterministische 12-karakter hex fingerprint voor een exception.
    /// Identieke fouten (zelfde type, genormaliseerd bericht, zelfde callsite) geven altijd
    /// dezelfde fingerprint — essentieel voor deduplicatie van GitHub Issues.
    /// <para>
    /// De fingerprint is een SHA-256-hash en wordt afgekapt op 12 hex-tekens; het onderliggende
    /// foutbericht is er niet uit terug te halen en komt nergens in het publieke issue terecht
    /// (#1008).
    /// </para>
    /// </summary>
    internal static string ComputeFingerprint(Exception ex, string eigenNamespacePrefix)
    {
        var raw = $"{ex.GetType().FullName}|{NormalizeMessage(ex.Message)}|{GetCallerFrame(ex, eigenNamespacePrefix)}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..12].ToLower();
    }

    private static string NormalizeMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        // Verwijder variabele delen zodat dezelfde fout altijd dezelfde fingerprint geeft
        var s = message;
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", "<guid>");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2})?", "<date>");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b\d+\b", "<n>");
        return s.Trim();
    }

    private static string GetCallerFrame(Exception ex, string eigenNamespacePrefix)
    {
        if (ex.StackTrace == null) return "unknown";
        foreach (var line in ex.StackTrace.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("at " + eigenNamespacePrefix, StringComparison.Ordinal))
                return trimmed.Split('(')[0].Replace("at ", "").Trim();
        }
        return "external";
    }

    private static HttpClient BuildHttpClient(string pat)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SportlinkFunction/2.0");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    // Bovengrens aan het aantal pagina's per zoekslag — voorkomt onbegrensd doorbladeren.
    // 5 pagina's × 100 = 500 issues per zoekslag, hoogstens drie zoekslagen.
    private const int SearchMaxPages = 5;
    private const int SearchPerPage = 100;

    /// <summary>
    /// Zoekslagen van de dedup-lookup (#1711), in deze volgorde: issues met het herkomstlabel
    /// (meldingen vanaf #1711), issues met het oude label <c>bug</c> (meldingen van daarvoor) en
    /// tot slot zonder labelfilter de recentste issues, zodat ook een melding wordt gevonden
    /// waarvan iemand de labels heeft gewijzigd. <c>null</c> = geen labelfilter.
    /// </summary>
    private static readonly string?[] ZoekLabelFilters = { HerkomstLabel, OudLabel, null };

    /// <summary>
    /// Zoekt een bestaand issue (open of gesloten) met de fingerprint-tag <c>[fp:{fp}]</c> in de
    /// titel. Gebruikt bewust de Issues List API in plaats van de GitHub Search API — zie #830
    /// voor de reden (Search API onbetrouwbaar met een fine-grained PAT die alleen
    /// <c>issues:write</c>-scope heeft). De zoekslagen staan in <see cref="ZoekLabelFilters"/>;
    /// een mislukte aanroep stopt de hele lookup (<c>null</c>, zoals vóór #1711).
    /// <c>internal</c> zodat Planner.Shared.Tests deze dedup-lookup rechtstreeks kan afdekken
    /// (InternalsVisibleTo) zonder de publieke <see cref="ReportAsync"/>-signatuur te hoeven
    /// verbouwen.
    /// </summary>
    internal static async Task<(int number, bool isClosed)?> SearchIssueAsync(
        HttpClient http, string owner, string repo, string fp, ILogger log)
    {
        foreach (var labelFilter in ZoekLabelFilters)
        {
            var (mislukt, treffer) = await ZoekInIssuelijstAsync(http, owner, repo, fp, labelFilter, log);
            if (mislukt) return null;
            if (treffer.HasValue) return treffer;
        }
        return null;
    }

    /// <summary>
    /// Vertrouwde auteurs voor de dedup-lookup (#1732): <c>author_association</c> OWNER, MEMBER of
    /// COLLABORATOR (het account achter de PAT heeft schrijfrechten en valt hier ook onder).
    /// Ontbrekend of onbekend = niet vertrouwd.
    /// </summary>
    internal static bool IsVertrouwdeAuteur(string? authorAssociation)
        => authorAssociation is "OWNER" or "MEMBER" or "COLLABORATOR";

    private static async Task<(bool mislukt, (int number, bool isClosed)? treffer)> ZoekInIssuelijstAsync(
        HttpClient http, string owner, string repo, string fp, string? labelFilter, ILogger log)
    {
        var marker = $"[fp:{fp}]";
        var filter = labelFilter is null ? "" : "&labels=" + Uri.EscapeDataString(labelFilter);

        for (var page = 1; page <= SearchMaxPages; page++)
        {
            // state=all → doorzoekt open én gesloten issues, zodat een eerder gesloten issue
            // (de fout is opnieuw opgetreden) ook gevonden wordt in plaats van een duplicaat aan
            // te maken.
            var url = "https://api.github.com/repos/" + owner + "/" + repo + "/issues"
                    + $"?state=all{filter}&per_page={SearchPerPage}&page={page}&sort=created&direction=desc";

            HttpResponseMessage resp;
            try
            {
                resp = await http.GetAsync(url);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "GitHub issues-lijst API: netwerkfout bij opzoeken fp:{Fp}", fp);
                return (true, null);
            }

            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("GitHub issues-lijst API: HTTP {Status} bij opzoeken fp:{Fp}", (int)resp.StatusCode, fp);
                return (true, null);
            }

            var json = await resp.Content.ReadAsStringAsync();
            var items = JArray.Parse(json);
            if (items.Count == 0) break; // geen resultaten meer

            foreach (var token in items)
            {
                if (token is not JObject item) continue;
                // De issues-lijst bevat ook pull requests; die hebben een 'pull_request'-veld.
                if (item["pull_request"] != null) continue;

                // #1732: onvertrouwde invoer bepaalt waar een melding terechtkomt; alleen issues van
                // een vertrouwde auteur tellen mee, in elke zoekslag (ook zonder labelfilter).
                if (!IsVertrouwdeAuteur(item["author_association"]?.Value<string>())) continue;

                var title = item["title"]?.Value<string>();
                if (title == null || !title.Contains(marker, StringComparison.Ordinal)) continue;

                var number = item["number"]!.Value<int>();
                var state = item["state"]!.Value<string>();
                var isClosed = state == "closed";
                log.LogInformation("Bestaand GitHub issue #{Nr} ({State}) gevonden voor fp:{Fp}", number, state, fp);
                return (false, (number, isClosed));
            }

            if (items.Count < SearchPerPage) break; // laatste pagina bereikt
        }

        return (false, null);
    }

    private static async Task ReopenIssueAsync(
        HttpClient http, string owner, string repo, int issueNumber, ILogger log)
    {
        var payload = JsonConvert.SerializeObject(new { state = "open" });
        var url = $"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}";
        var resp = await http.PatchAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
        if (resp.IsSuccessStatusCode)
            log.LogInformation("GitHub issue #{Nr} heropend (opnieuw opgetreden)", issueNumber);
        else
            log.LogWarning("GitHub issue heropenen mislukt: HTTP {Status}", (int)resp.StatusCode);
    }

    /// <summary>
    /// Reageert op een recidiverende exception. <c>internal</c> zodat Planner.Shared.Tests de
    /// daadwerkelijk verzonden comment-body kan afdekken (InternalsVisibleTo) —
    /// regressietest voor #1008: de body mag nooit vrije <c>ex.Message</c>/stacktrace-tekst
    /// bevatten, alleen de vaste allowlist-velden uit <see cref="BuildPublicDiagnostics"/>.
    /// </summary>
    internal static async Task AddCommentAsync(
        HttpClient http, string owner, string repo, int issueNumber,
        Exception ex, string functionName, string fp, ILogger log)
    {
        var body = "🔁 Opnieuw opgetreden\n\n" + BuildPublicDiagnostics(ex, functionName, fp, NederlandseTijdNu());

        var payload = JsonConvert.SerializeObject(new { body });
        var url = $"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}/comments";
        var resp = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));

        if (resp.IsSuccessStatusCode)
            log.LogInformation("Comment toegevoegd aan GitHub issue #{Nr}", issueNumber);
        else
            log.LogWarning("GitHub comment API: HTTP {Status}", (int)resp.StatusCode);
    }

    /// <summary>
    /// Maakt een nieuw issue aan. <c>internal</c> zodat Planner.Shared.Tests de daadwerkelijk
    /// verzonden issue-titel/body kan afdekken (InternalsVisibleTo) — regressietest
    /// voor #1008: titel en body mogen nooit vrije <c>ex.Message</c>/stacktrace-tekst bevatten,
    /// alleen de vaste allowlist-velden uit <see cref="BuildPublicDiagnostics"/>.
    /// </summary>
    internal static async Task CreateIssueAsync(
        HttpClient http, string owner, string repo, string fp,
        Exception ex, string functionName, ILogger log)
    {
        var title = BuildPublicTitle(ex, fp);
        var body = "## Automatisch gerapporteerde exception\n\n"
                 + BuildPublicDiagnostics(ex, functionName, fp, NederlandseTijdNu())
                 + "\n\n*Automatisch aangemaakt door GitHubIssueReporter (v2.1 zelfherstellend systeem)*";

        var payload = JsonConvert.SerializeObject(new
        {
            title,
            body,
            labels = NieuwIssueLabels
        });

        var url = $"https://api.github.com/repos/{owner}/{repo}/issues";
        var resp = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("GitHub issue aanmaken mislukt: HTTP {Status}", (int)resp.StatusCode);
            return;
        }

        var json = await resp.Content.ReadAsStringAsync();
        var nummer = JObject.Parse(json)["number"]?.Value<int>();
        if (nummer is not int issueNummer)
        {
            log.LogWarning("GitHub issue aangemaakt voor fp:{Fp}, maar zonder nummer in het antwoord — opdrachtregel niet geplaatst", fp);
            return;
        }
        log.LogInformation("GitHub issue #{Nr} aangemaakt voor fp:{Fp}", issueNummer, fp);
        await PlaatsOpdrachtRegelAsync(http, owner, repo, issueNummer, log);
    }

    private static async Task PlaatsOpdrachtRegelAsync(
        HttpClient http, string owner, string repo, int issueNumber, ILogger log)
    {
        var payload = JsonConvert.SerializeObject(new { body = OpdrachtRegel });
        var url = $"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}/comments";
        var resp = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode)
            log.LogWarning("Opdrachtregel op GitHub issue #{Nr} plaatsen mislukt: HTTP {Status}", issueNumber, (int)resp.StatusCode);
    }

    private static DateTime NederlandseTijdNu()
    {
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nlZone);
    }

    /// <summary>
    /// Bouwt de publieke issue-titel. Bevat uitsluitend het exceptietype en de fingerprint-tag —
    /// nooit <c>ex.Message</c> (#1008: vrije foutteksten kunnen databasenamen, servernamen of
    /// andere identificerende inhoud bevatten die geen enkel bestaand denylist-patroon dekt).
    /// <c>internal</c> zodat Planner.Shared.Tests dit rechtstreeks kan afdekken (InternalsVisibleTo).
    /// </summary>
    internal static string BuildPublicTitle(Exception ex, string fp)
        => $"[bug][fp:{fp}] {ClassifyErrorCategory(ex)}: {ex.GetType().Name}";

    /// <summary>
    /// Bouwt het publieke diagnostiek-blok volgens het allowlist-model uit #1008: uitsluitend
    /// vaste technische velden (foutcategorie, exceptietype, interne operationele naam — de
    /// Azure Function-naam — veilige fingerprint/hash, tijdstip). Vrije <c>ex.Message</c>,
    /// inner-exceptietekst en bronpaden/stacktrace worden NOOIT overgenomen — die blijven
    /// uitsluitend in de structured logging/Application Insights van de betreffende Function App
    /// (zie de <c>log.LogError(ex, ...)</c>-aanroep vóór <see cref="ReportAsync"/> in de
    /// tier-specifieke catch-blokken), nooit in dit publieke GitHub-issue.
    /// <para>
    /// <b>Let op voor toekomstige aanroepers:</b> <paramref name="functionName"/> is de enige
    /// door de aanroeper aangeleverde vrije tekst in dit blok. Geef daar uitsluitend een vaste
    /// literal mee (de Azure Function-naam) — nooit een clubcode, e-mailadres, teamnaam of andere
    /// runtimewaarde; deze repository is publiek en de aangemaakte issues dus ook.
    /// </para>
    /// <c>internal</c> zodat Planner.Shared.Tests dit rechtstreeks kan afdekken (InternalsVisibleTo).
    /// </summary>
    internal static string BuildPublicDiagnostics(Exception ex, string functionName, string fp, DateTime nlTijd)
    {
        var innerType = ex.InnerException?.GetType().FullName ?? "(geen)";
        return $"**Foutcategorie:** {ClassifyErrorCategory(ex)}\n"
             + $"**Exceptietype:** `{ex.GetType().FullName}`\n"
             + $"**Inner exceptietype:** `{innerType}`\n"
             + $"**Interne operationele naam:** `{functionName}`\n"
             + $"**Fingerprint:** `{fp}`\n"
             + $"**Tijdstip:** {nlTijd:dd-MM-yyyy HH:mm} (Europe/Amsterdam)\n\n"
             + "*Volledige diagnostiek (foutbericht, stacktrace, bronpaden) staat uitsluitend in de "
             + "structured logging/Application Insights van deze Function App — nooit in dit publieke issue.*";
    }

    /// <summary>
    /// Classificeert een exception (incl. inner exceptions) naar een vaste, veilige categorie —
    /// onderdeel van het allowlist-model van #1008. Doorloopt de inner-exceptionketen zodat een
    /// gewrapte SQL-fout (bv. via een repository-laag) ook als "Database" herkend wordt.
    /// <para>
    /// De <c>"Sql"</c>-toets dekt beide tiers: <c>Microsoft.Data.SqlClient.SqlException</c> zowel
    /// als <c>Npgsql.PostgresException</c>/<c>Npgsql.NpgsqlException</c> — "Npgsql" bevat
    /// hoofdletterongevoelig "sql". Dat is geen toeval waar we op leunen zonder het te bewijzen:
    /// <c>GitHubIssueReporterTests.ClassifyErrorCategory_NpgsqlAchtigeException_...</c> legt het
    /// vast.
    /// </para>
    /// </summary>
    private static string ClassifyErrorCategory(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            var typeName = current.GetType().FullName ?? "";
            if (typeName.Contains("Sql", StringComparison.OrdinalIgnoreCase))
                return "Database";
            if (current is TimeoutException or TaskCanceledException or OperationCanceledException)
                return "Timeout";
            if (current is HttpRequestException or System.Net.WebException or System.Net.Sockets.SocketException)
                return "Netwerk";
            if (current is InvalidOperationException or ArgumentException or FormatException)
                return "Configuratie/status";
        }
        return "Onbekend";
    }
}
