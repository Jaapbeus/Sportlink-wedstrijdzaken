namespace Planner.Shared.Email;

/// <summary>Eenvoudig e-mailsjabloon voor de antwoordgenerator.</summary>
public record EmailTemplate(string Key, string Onderwerp, string Body) : IAntwoordSjabloon;
