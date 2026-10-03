namespace Planner.Shared.Feedback;

/// <summary>Invoercontrole en sanitizing van feedbacktekst (#1494: afgesplitst uit FeedbackCore).</summary>
internal static class FeedbackTekst
{
    internal static string BouwQaBlok(List<VraagAntwoord>? qaList)
    {
        if (qaList == null || qaList.Count == 0) return "";
        var sb = new System.Text.StringBuilder("\nAanvullende context:\n");
        foreach (var qa in qaList)
            sb.AppendLine($"- {Sanitize(qa.Vraag, 200)}: {Sanitize(qa.Antwoord, 500)}");
        return sb.ToString();
    }

    /// <summary>
    /// Verzamelt alle velden van een <see cref="FeedbackRequest"/> die ooit in een AI-prompt of in de
    /// gepubliceerde GitHub-body terechtkomen, zodat de PII-gate de volledige invoer controleert in
    /// plaats van alleen Beschrijving + Antwoord (#1006 — de oorspronkelijke #427-gate miste
    /// Context.Pagina/Versie/Browser en elke Vraag). Type is sinds #1127 ook opgenomen: de
    /// <see cref="FeedbackCore.IsOngeldigType"/>-check hierboven maakt PII in Type al structureel onmogelijk, maar
    /// deze verzameling blijft Type meenemen als extra, onafhankelijke laag — mocht die allowlist ooit
    /// verdwijnen, dan blokkeert deze gate nog steeds.
    /// </summary>
    internal static string VerzamelTeCheckenTekst(FeedbackRequest dto)
    {
        var delen = new List<string?> { dto.Type, dto.Beschrijving, dto.Context?.Pagina, dto.Context?.Versie, dto.Context?.Browser };
        if (dto.VragenAntwoorden != null)
        {
            foreach (var qa in dto.VragenAntwoorden)
            {
                delen.Add(qa.Vraag);
                delen.Add(qa.Antwoord);
            }
        }
        return string.Join(" ", delen.Where(d => !string.IsNullOrWhiteSpace(d)));
    }

    // PII-gate: detecteert e-mailadressen en Nederlandse telefoonnummers. (#427, uitgebreid #1006)
    // Blokkeert publicatie naar GitHub als mogelijke persoonsgegevens aanwezig zijn.
    // Let op: dit is regex-detectie van e-mail/telefoon — geen algemene garantie tegen elke vorm van
    // persoonsgegevens of secrets (bijv. namen, adressen, BSN's worden niet herkend).
    internal static bool BevatPii(string tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst)) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(tekst,
            @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}"))
            return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(tekst,
            @"(\+31|0031|06)[\s\-]?\d{2}[\s\-]?\d{6,8}|0\d{1,2}[\s\-]\d{6,8}"))
            return true;
        return false;
    }

    internal static string Sanitize(string? input, int maxLen)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var clean = input
            .Replace("<script", "&lt;script", StringComparison.OrdinalIgnoreCase)
            .Replace("</script>", "&lt;/script&gt;", StringComparison.OrdinalIgnoreCase);
        return clean.Length > maxLen ? clean[..maxLen] + "…" : clean;
    }

    private static readonly System.Text.RegularExpressions.Regex OnveiligeVersieTekens =
        new(@"[^A-Za-z0-9._ ()/\-]", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Pagina: geen backticks (breekt de code-span), regeleinden of | (breekt de tabelrij), [ ] (link-
    // markdown), < > (HTML) of @ (mention). Daarna dezelfde Sanitize als alle andere tekst.
    private static readonly System.Text.RegularExpressions.Regex OnveiligePaginaTekens =
        new(@"[`\r\n|\[\]<>@]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Versie en Browser komen rechtstreeks van de client en staan in een tabelcel van een openbaar
    /// issue: alleen letters, cijfers en <c>._ ()/-</c>, maximaal 60 tekens (#1494).
    /// </summary>
    internal static string SanitizeVersieOfBrowser(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var schoon = OnveiligeVersieTekens.Replace(input, "").Trim();
        return schoon.Length > 60 ? schoon[..60] : schoon;
    }

    /// <summary>Pagina in een code-span in de issuetabel: geen markdown-, tabel-, HTML- of mention-tekens (#1494).</summary>
    internal static string SanitizePagina(string? input) =>
        Sanitize(OnveiligePaginaTekens.Replace(input ?? "", ""), 200);
}
