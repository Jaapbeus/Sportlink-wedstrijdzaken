using System.Text;
using static Planner.Shared.Feedback.FeedbackAi;
using static Planner.Shared.Feedback.FeedbackTekst;

namespace Planner.Shared.Feedback;

/// <summary>Bouwt de publieke GitHub-issuebody (#1494: afgesplitst uit FeedbackCore).</summary>
internal static class FeedbackIssueBody
{
    internal static string Bouw(FeedbackRequest dto, StructuredIssue structured, DateTime tijdstipUtc)
    {
        var typeIcon = dto.Type switch { "Fout" => "🐛", "Verzoek" => "💡", _ => "❓" };
        var ctx = dto.Context;
        var beschrijving = Sanitize(dto.Beschrijving, 2000);
        var versie = SanitizeVersieOfBrowser(ctx?.Versie);
        var tijdstip = tijdstipUtc.ToString("yyyy-MM-dd HH:mm") + " UTC";

        var sb = new StringBuilder();
        sb.AppendLine("## 🗣️ Gemeld via feedback widget");
        sb.AppendLine();
        sb.AppendLine("| Veld | Waarde |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Type | {typeIcon} {dto.Type} |");
        if (ctx != null)
        {
            sb.AppendLine($"| Pagina | `{SanitizePagina(ctx.Pagina)}` |");
            sb.AppendLine($"| Versie | {versie} |");
            sb.AppendLine($"| Omgeving | {(versie.Contains("dev", StringComparison.OrdinalIgnoreCase) ? "ontwikkeling" : "productie")} |");
            var browser = SanitizeVersieOfBrowser(ctx.Browser);
            if (browser.Length > 0)
                sb.AppendLine($"| Browser | {browser} |");
        }
        sb.AppendLine($"| Tijdstip | {tijdstip} |");
        sb.AppendLine();

        sb.AppendLine("## Beschrijving (eigen woorden gebruiker)");
        sb.AppendLine();
        sb.AppendLine($"> {beschrijving.Replace("\n", "\n> ")}");
        sb.AppendLine();

        if (dto.VragenAntwoorden?.Count > 0)
        {
            sb.AppendLine("## Aanvullende context");
            sb.AppendLine();
            foreach (var qa in dto.VragenAntwoorden)
            {
                var vraag = Sanitize(qa.Vraag, 200);
                var antwoord = Sanitize(qa.Antwoord, 500);
                sb.AppendLine($"**{vraag}:** {antwoord}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Analyse");
        sb.AppendLine();
        sb.AppendLine(structured.Samenvatting);
        sb.AppendLine();

        if (structured.Acceptatiecriteria.Count > 0)
        {
            sb.AppendLine("## Acceptatiecriteria");
            sb.AppendLine();
            foreach (var criterium in structured.Acceptatiecriteria)
                sb.AppendLine($"- [ ] {Sanitize(criterium, MaxAcceptatiecriteriumLengte)}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine($"*Aangemaakt via BlazorAdmin feedback widget v{(ctx is null ? "?" : versie)}*");

        return sb.ToString();
    }
}
