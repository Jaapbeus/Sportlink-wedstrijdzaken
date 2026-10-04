using System.Text;
using static Planner.Shared.Feedback.FeedbackAi;
using static Planner.Shared.Feedback.FeedbackTekst;

namespace Planner.Shared.Feedback;

/// <summary>Bouwt de publieke GitHub-issuebody (#1494: afgesplitst uit FeedbackCore).</summary>
internal static class FeedbackIssueBody
{
    internal static string Bouw(FeedbackRequest dto, StructuredIssue structured, DateTime tijdstipUtc)
    {
        var ctx = dto.Context;
        var versie = SanitizeVersieOfBrowser(ctx?.Versie);

        var sb = new StringBuilder();
        VoegMetaTabelToe(sb, dto, versie, tijdstipUtc);

        sb.AppendLine("## Beschrijving (eigen woorden gebruiker)");
        sb.AppendLine();
        sb.AppendLine($"> {Sanitize(dto.Beschrijving, 2000).Replace("\n", "\n> ")}");
        sb.AppendLine();

        VoegVragenToe(sb, dto.VragenAntwoorden);

        sb.AppendLine("## Analyse");
        sb.AppendLine();
        sb.AppendLine(structured.Samenvatting);
        sb.AppendLine();

        VoegAcceptatiecriteriaToe(sb, structured.Acceptatiecriteria);

        sb.AppendLine("---");
        sb.AppendLine($"*Aangemaakt via BlazorAdmin feedback widget v{(ctx is null ? "?" : versie)}*");

        return sb.ToString();
    }

    private static void VoegMetaTabelToe(StringBuilder sb, FeedbackRequest dto, string versie, DateTime tijdstipUtc)
    {
        var typeIcon = dto.Type switch { "Fout" => "🐛", "Verzoek" => "💡", _ => "❓" };
        sb.AppendLine("## 🗣️ Gemeld via feedback widget");
        sb.AppendLine();
        sb.AppendLine("| Veld | Waarde |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Type | {typeIcon} {dto.Type} |");
        var ctx = dto.Context;
        if (ctx != null)
        {
            sb.AppendLine($"| Pagina | `{SanitizePagina(ctx.Pagina)}` |");
            sb.AppendLine($"| Versie | {versie} |");
            sb.AppendLine($"| Omgeving | {(versie.Contains("dev", StringComparison.OrdinalIgnoreCase) ? "ontwikkeling" : "productie")} |");
            var browser = SanitizeVersieOfBrowser(ctx.Browser);
            if (browser.Length > 0)
                sb.AppendLine($"| Browser | {browser} |");
        }
        sb.AppendLine($"| Tijdstip | {tijdstipUtc:yyyy-MM-dd HH:mm} UTC |");
        sb.AppendLine();
    }

    private static void VoegVragenToe(StringBuilder sb, IReadOnlyCollection<VraagAntwoord>? vragenAntwoorden)
    {
        if (vragenAntwoorden is not { Count: > 0 }) return;
        sb.AppendLine("## Aanvullende context");
        sb.AppendLine();
        foreach (var qa in vragenAntwoorden)
        {
            sb.AppendLine($"**{Sanitize(qa.Vraag, 200)}:** {Sanitize(qa.Antwoord, 500)}");
            sb.AppendLine();
        }
    }

    private static void VoegAcceptatiecriteriaToe(StringBuilder sb, IReadOnlyCollection<string> criteria)
    {
        if (criteria.Count == 0) return;
        sb.AppendLine("## Acceptatiecriteria");
        sb.AppendLine();
        foreach (var criterium in criteria)
            sb.AppendLine($"- [ ] {Sanitize(criterium, MaxAcceptatiecriteriumLengte)}");
        sb.AppendLine();
    }
}
