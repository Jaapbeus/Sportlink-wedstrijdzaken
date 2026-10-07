using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email.Trace;

/// <summary>Besluit van de zekerheidspoort: moet het antwoord worden tegengehouden?</summary>
public sealed record ZekerheidsPoortBesluit(bool Tegenhouden, IReadOnlyList<string> Redenen)
{
    public static readonly ZekerheidsPoortBesluit Doorlaten = new(false, Array.Empty<string>());
}

/// <summary>
/// Zekerheidspoort (#1568, deel D; besluit eigenaar): is het beoordeelde antwoord <c>Onzeker</c> of
/// <c>Mislukt</c>, dan gaat er géén automatisch antwoord naar de afzender maar krijgt de verwerking
/// status Review. De beslissing (en de tekst van de review-mail) staan hier, op één plek, zodat beide
/// tiers identiek beslissen; de tierbestanden doen alleen de persistentie en het versturen.
/// </summary>
public static class ZekerheidsPoort
{
    /// <summary>Naam van de clubinstelling (<c>AppSettings</c>); ontbrekend = aan.</summary>
    public const string InstellingNaam = "ZekerheidspoortActief";

    /// <summary>Vaste tekst bovenaan de mail naar de review-ontvanger: geen persoonsgegevens, geen trace-details.</summary>
    public const string ReviewKop =
        "LET OP: dit antwoord is NIET naar de afzender verstuurd. De zekerheidspoort hield het tegen omdat het "
        + "systeem niet zeker genoeg is van het antwoord. Bekijk de beslissingstrace in het e-maillog van de "
        + "Admin GUI en stuur zo nodig zelf een antwoord.";

    /// <summary>
    /// Fail-safe lezer voor de tier-databasevraag: een ontbrekende rij of kolom, of een databasefout, telt als
    /// AAN — liever een mens laten kijken dan een onzeker antwoord versturen. Alleen een expliciet <c>false</c> zet uit.
    /// </summary>
    public static async Task<bool> LeesVeiligAsync(Func<Task<object?>> leesWaarde, ILogger log)
    {
        try { return await leesWaarde() is not false; }
        catch (Exception ex)
        {
            log.LogWarning("Zekerheidspoort-instelling niet leesbaar ({Fouttype}) — de poort geldt als aan", ex.GetType().Name);
            return true;
        }
    }

    /// <summary>Leest de instelling: alleen een expliciete uit-waarde zet de poort uit; leeg of onbekend = aan.</summary>
    public static bool IsActief(string? waarde) => waarde?.Trim().ToLowerInvariant() is not ("0" or "false");

    /// <summary>
    /// Beoordeelt de trace tot nu toe en legt de uitkomst vast als stap <c>zekerheidspoort</c>. Zonder trace
    /// is er niets om te beoordelen en gaat het antwoord door; bij een uitgeschakelde poort wordt dat ook
    /// in de trace vermeld als er iets onzeker was.
    /// </summary>
    public static ZekerheidsPoortBesluit Bepaal(bool actief, TraceBuilder? trace)
    {
        if (trace is null) return ZekerheidsPoortBesluit.Doorlaten;
        var oordeel = ZekerheidsBeoordeling.Beoordeel(trace.Stappen);
        if (oordeel.IsZeker) return ZekerheidsPoortBesluit.Doorlaten;

        var redenen = TraceBuilder.Saneer(string.Join("; ", oordeel.Redenen), 300);
        trace.Voeg(TraceCodes.Zekerheidspoort, "Zekerheidspoort",
            actief ? "Tegengehouden: antwoord niet naar de afzender" : "Poort staat uit: antwoord toch verstuurd",
            ZekerheidsNiveau.Onzeker,
            new[] { new KeyValuePair<string, string?>("redenen", redenen),
                    new KeyValuePair<string, string?>("poortActief", actief ? "ja" : "nee") });
        return actief ? new ZekerheidsPoortBesluit(true, oordeel.Redenen) : ZekerheidsPoortBesluit.Doorlaten;
    }

    /// <summary>Zet de vaste kop boven het voorgestelde antwoord voor de review-ontvanger.</summary>
    public static string MetReviewKop(string voorgesteldeBody) => ReviewKop + "\n\n" + voorgesteldeBody;
}
