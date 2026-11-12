namespace Planner.Shared.Infrastructure;

public static partial class GitHubIssueReporter
{
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
        // #1779: faselabels zijn vaste literals (SyncUitkomst.Fase*), dus veilig in het publieke blok.
        var fases = ex is Planner.Shared.Sync.SyncGedeeltelijkMisluktException sync
            ? $"**Mislukte fases:** {string.Join(", ", sync.Fases)}\n"
            : "";
        return $"**Foutcategorie:** {ClassifyErrorCategory(ex)}\n"
             + fases
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
            if (current is Planner.Shared.Sync.SyncGedeeltelijkMisluktException)
                return "Sync gedeeltelijk mislukt";
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
