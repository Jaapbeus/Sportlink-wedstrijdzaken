namespace Planner.Shared.Feedback;

// Opslagcontract van de feedbackmeldingen (#764), gedeeld tussen beide database-tiers.
//
// Dit is GEEN runtime-providerswitch (regel 2 van de multi-tier-strategie): elke tier heeft zijn
// eigen, volledig gescheiden implementatie (SQL Server: FunctionApp/Feedback/SqlFeedbackStore,
// Postgres: FunctionApp.Postgres/Feedback/PostgresFeedbackStore) en geeft die door aan de gedeelde
// orkestratie in Planner.Endpoints — dezelfde vorm als ISportlinkMutationAuditService bij
// SportlinkEndpointSupportCore. Alles wat niet over databasetoegang gaat (rate limiting,
// publicatiebeleid, redactie, bewaartermijnen) staat eenmalig in Planner.Shared/Planner.Endpoints.

/// <summary>Statuswaarden van <c>avg.Feedback.Status</c>. Bewust geen "open"/"gesloten": die zouden GitHub spiegelen zonder synchronisatie.</summary>
public static class FeedbackStatusWaarden
{
    public const string WachtOpPublicatie = "wacht-op-publicatie";
    public const string Publiceren = "publiceren";
    public const string Gepubliceerd = "gepubliceerd";
    public const string GitHubMislukt = "github-mislukt";

    public static readonly string[] Alle = [WachtOpPublicatie, Publiceren, Gepubliceerd, GitHubMislukt];
}

/// <summary>Bewaartermijnen (#764, eigenaarsbesluit 2026-10-03) — één plek, door beide tiers gebruikt.</summary>
public static class FeedbackRetentie
{
    /// <summary>Identiteit van de melder: zolang het issue open is, plus dit aantal maanden na sluiting.</summary>
    public const int IdentiteitNaSluitingMaanden = 24;

    /// <summary>Technische context: console-fouten, mislukte aanroepen, navigatiespoor.</summary>
    public const int TelemetrieDagen = 90;

    /// <summary>Inzagelog: wie bekeek welke melding.</summary>
    public const int InzageLogMaanden = 24;

    /// <summary>Maximaal aantal GitHub-statusopvragen per retentierun (rate limit en looptijd).</summary>
    public const int MaxStatusControlesPerRun = 200;
}

public sealed record FeedbackNieuw(
    Guid FeedbackId,
    string ClubCode,
    string Type,
    string Onderwerp,
    string IssueBody,
    string Beschrijving,
    string? VragenAntwoordenJson,
    string? MelderObjectId,
    string? MelderNaam,
    string MelderRol,
    string? Pagina,
    string? AppVersie,
    string Status);

public sealed record FeedbackTelemetrieRegel(string Bron, string Payload);

public sealed record FeedbackSamenvatting(
    Guid FeedbackId,
    DateTime AangemaaktUtc,
    string Type,
    string Onderwerp,
    string? MelderNaam,
    bool IsGeanonimiseerd,
    string MelderRol,
    string? Pagina,
    int? IssueNummer,
    string? IssueUrl,
    string Status);

public sealed record FeedbackDetail(
    FeedbackSamenvatting Samenvatting,
    string Beschrijving,
    string? VragenAntwoordenJson,
    string IssueBody,
    string? AppVersie,
    IReadOnlyList<FeedbackTelemetrieRegel> Telemetrie);

public sealed record FeedbackFilter(
    string? Type, string? Status, DateTime? VanafUtc, DateTime? TotUtc, string? Zoek, int Limit, int Offset);

public sealed record FeedbackLijstResultaat(IReadOnlyList<FeedbackSamenvatting> Items, int Totaal);

/// <summary>Eén inzage in het overzicht — wie, wat, wanneer. Bevat nooit de inhoud van de melding.</summary>
public sealed record FeedbackInzageNieuw(
    string ClubCode, string? InzienDoorObjectId, string? InzienDoorNaam, string Actie, Guid? FeedbackId, string? Filter);

public sealed record FeedbackInzageRegel(
    DateTime TijdstipUtc, string? InzienDoorNaam, string Actie, Guid? FeedbackId, string? Filter);

public sealed record FeedbackInzageLijst(IReadOnlyList<FeedbackInzageRegel> Items, int Totaal);

/// <summary>Een gepubliceerd issue waarvan de sluitingsstatus nog gecontroleerd moet worden.</summary>
public sealed record FeedbackIssueVerwijzing(Guid FeedbackId, int IssueNummer);

/// <summary>Aantallen van één retentierun — alleen tellingen, nooit inhoud (AVG).</summary>
public sealed record FeedbackRetentieResultaat(int Geanonimiseerd, int TelemetrieVerwijderd, int InzageVerwijderd);

public interface IFeedbackStore
{
    /// <summary>Aantal meldingen sinds <paramref name="sindsUtc"/>; zonder <paramref name="melderObjectId"/> voor de hele club (vangnet).</summary>
    Task<int> TelRecenteMeldingenAsync(string clubCode, string? melderObjectId, DateTime sindsUtc);

    /// <summary>Bewaart de melding en haar telemetrie in één transactie.</summary>
    Task BewaarAsync(FeedbackNieuw nieuw, IReadOnlyList<FeedbackTelemetrieRegel> telemetrie);

    /// <summary>Claimt een melding voor publicatie (wacht/mislukt → publiceren). Geeft <c>false</c> als hij al gepubliceerd wordt of is — voorkomt een dubbel issue bij twee klikken.</summary>
    Task<bool> ClaimPublicatieAsync(string clubCode, Guid feedbackId);

    Task ZetGepubliceerdAsync(string clubCode, Guid feedbackId, int issueNummer, string issueUrl);

    Task ZetPublicatieMisluktAsync(string clubCode, Guid feedbackId);

    Task<FeedbackLijstResultaat> LijstAsync(string clubCode, FeedbackFilter filter);

    Task<FeedbackDetail?> GetDetailAsync(string clubCode, Guid feedbackId);

    Task LogInzageAsync(FeedbackInzageNieuw inzage);

    Task<FeedbackInzageLijst> LijstInzageAsync(string clubCode, int limit, int offset);

    // ── Bewaartermijn (#764) — over alle clubs, door de retentietimer ──

    /// <summary>
    /// Gepubliceerde, niet-geanonimiseerde meldingen waarvan de issuestatus het langst niet is
    /// gecontroleerd (nooit gecontroleerd eerst). Ook reeds gesloten issues horen erbij: een
    /// heropend issue moet de bewaartermijn weer laten vervallen ("zolang het issue open is").
    /// </summary>
    Task<IReadOnlyList<FeedbackIssueVerwijzing>> TeControlerenIssuesAsync(int max);

    /// <summary>Legt de gecontroleerde issuestatus vast: sluitmoment, of <c>null</c> als het issue (weer) open is.</summary>
    Task ZetIssueStatusAsync(Guid feedbackId, DateTime? geslotenOpUtc);

    /// <summary>Anonimiseert identiteit, wist oude telemetrie en oude inzageregels.</summary>
    Task<FeedbackRetentieResultaat> VoerRetentieUitAsync(DateTime nuUtc);
}
