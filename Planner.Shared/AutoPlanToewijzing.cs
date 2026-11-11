namespace Planner.Shared;

/// <summary>Wire row for the visible plan, shared by the browser and both database tiers.</summary>
public sealed class AutoPlanToewijzing
{
    public long? WedstrijdCode { get; set; }
    public string? HuidigeVeld { get; set; }
    public string? HuidigeTijd { get; set; }
    public int? VeldNummer { get; set; }
    public string? VeldSubpositie { get; set; }
    public string? Tijd { get; set; }
}
