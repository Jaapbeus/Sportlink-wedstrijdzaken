using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email;

/// <summary>Opbouw van het doorstuurbericht voor teambegeleiding, gedeeld door beide e-mailadapters (#1809).</summary>
public sealed record TeamContactDoorstuurBericht(string Onderwerp, string Inhoud, string? ReplyTo)
{
    public static TeamContactDoorstuurBericht Bouw(
        string teamNaam, string? afzenderNaam, string? afzenderAdres, string? body, ILogger log)
    {
        var teamVeilig = AiVeldSanering.TeamNaamOfGeneriek(teamNaam, false);
        var naamVeilig = AiVeldSanering.AfzenderNaamWeergave(afzenderNaam) ?? "afzender";
        var replyTo = AfzenderControle.IsGeschikt(afzenderAdres) ? afzenderAdres : null;
        var onderwerp = $"[{teamVeilig}] vraag van {naamVeilig}";
        var inhoud = $"Er is een vraag binnengekomen over de begeleiding van {teamVeilig}.\n\n"
                   + $"Vraag van: {naamVeilig}\n\n"
                   + $"---\n{BerichtBodyBegrenzing.Kap(body, log)}\n---\n\n"
                   + (replyTo is null
                       ? "Direct antwoorden op dit bericht is niet mogelijk; controleer het oorspronkelijke bericht en kies handmatig het juiste contactkanaal."
                       : "U kunt direct antwoorden op dit bericht — uw antwoord gaat naar de vraagsteller.");
        return new TeamContactDoorstuurBericht(onderwerp, inhoud, replyTo);
    }
}
