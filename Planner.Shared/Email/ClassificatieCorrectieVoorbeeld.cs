namespace Planner.Shared.Email;

/// <summary>Few-shot voorbeeld voor classificatie-leren via gevalideerde correcties (#323).</summary>
public sealed record ClassificatieCorrectieVoorbeeld(
    string OrigineelType,
    string JuistType,
    string OrigineleSamenvatting,
    string CorrectieSamenvatting);
