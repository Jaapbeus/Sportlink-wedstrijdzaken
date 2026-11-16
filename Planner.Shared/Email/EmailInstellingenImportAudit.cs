namespace Planner.Shared.Email;

/// <summary>Projecteert alleen gewijzigde importvelden naar de waarden voor het bestaande auditfilter.</summary>
public static class EmailInstellingenImportAudit
{
    public static IReadOnlyList<EmailInstellingenImportAuditVeld> Projecteer(
        EmailVerwerkingsInstellingen huidig,
        bool verwerking,
        bool review,
        string? ontvanger)
    {
        var velden = new List<EmailInstellingenImportAuditVeld>(3);
        VoegSchakelaarToe(velden, "EmailVerwerkingIngeschakeld", huidig.VerwerkingIngeschakeld, verwerking);
        VoegSchakelaarToe(velden, "EmailReviewModusActief", huidig.ReviewModusActief, review);
        if (!string.Equals(huidig.ReviewOntvanger, ontvanger, StringComparison.Ordinal))
            velden.Add(new("EmailReviewOntvanger", huidig.ReviewOntvanger, ontvanger));
        return velden;
    }

    private static void VoegSchakelaarToe(
        ICollection<EmailInstellingenImportAuditVeld> velden, string naam, bool oud, bool nieuw)
    {
        if (oud == nieuw) return;
        velden.Add(new(naam, oud ? "1" : "0", nieuw ? "1" : "0"));
    }
}

public sealed record EmailInstellingenImportAuditVeld(string Veld, string? OudeWaarde, string? NieuweWaarde);
