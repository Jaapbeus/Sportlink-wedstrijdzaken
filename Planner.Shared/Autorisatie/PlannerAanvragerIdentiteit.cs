using System.Text;

namespace Planner.Shared.Autorisatie;

/// <summary>Choose the minimal server-derived label stored for a planner requester.</summary>
public static class PlannerAanvragerIdentiteit
{
    private const int MaxLabelBytes = 200;

    public static string? BepaalOpgeslagenLabel(string? objectIdClaim, string? naamClaim, bool lokaleBypass)
    {
        if (lokaleBypass)
            return "lokale-ontwikkelaar";

        if (!Guid.TryParse(objectIdClaim, out var objectId) || objectId == Guid.Empty)
            return null;

        if (IsBruikbareWeergavenaam(naamClaim))
            return naamClaim!.Trim();

        return objectId.ToString("D");
    }

    public static bool ProbeerToepassen(
        string? objectIdClaim, string? naamClaim, bool lokaleBypass, Action<string> zetAanvrager)
    {
        var label = BepaalOpgeslagenLabel(objectIdClaim, naamClaim, lokaleBypass);
        if (label is null)
            return false;

        zetAanvrager(label);
        return true;
    }

    private static bool IsBruikbareWeergavenaam(string? naam)
    {
        if (string.IsNullOrWhiteSpace(naam) || naam.Any(char.IsControl))
            return false;

        var trimmed = naam.Trim();
        return !trimmed.Contains('@') && Encoding.UTF8.GetByteCount(trimmed) <= MaxLabelBytes;
    }
}
