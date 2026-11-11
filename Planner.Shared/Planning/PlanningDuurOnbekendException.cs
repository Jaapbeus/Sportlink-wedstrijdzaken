namespace Planner.Shared.Planning;

/// <summary>Een bestaande veldbezetting zonder betrouwbare duur blokkeert de planning.</summary>
public sealed class PlanningDuurOnbekendException : Exception
{
    public const string PubliekeMelding = "Planning geblokkeerd: wedstrijdduur onbekend voor bestaande veldbezetting.";

    public PlanningDuurOnbekendException() : base(PubliekeMelding) { }
}
