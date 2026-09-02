namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/ClubAppSettingsSnapshot.cs</c> (#889) —
/// woordelijke kopie. Club-specifieke instellingen-snapshot voor het dry-run pad van de
/// Email-tester (#677): laat een dry-run met de democlub geselecteerd de instellingen van díe
/// club gebruiken in plaats van de proces-globale <see cref="PostgresAppSettings"/>-cache (die
/// altijd de primaire club van deze deployment bevat).
///
/// Alle velden zijn bewust nullable: een ontbrekende waarde in <c>public.appsettings</c> voor de
/// opgegeven club betekent "geen waarde ingesteld voor déze club", nooit "val terug op de
/// instelling van een andere club".
/// </summary>
public sealed record ClubAppSettingsSnapshot(
    string? PlannerAfzenderNaam,
    string? CoordinatorNaam,
    string? CoordinatorFunctie,
    string? EmailVoetnoot,
    int? HerplanDeadlineDagen,
    // #561: verzet-zonder-datum flow (KNVB-bijlage + vrije-zaterdagen-voorzet) — niet vertaald op
    // deze tier (geen knvbStandaardRegio-instelling, geen KnvbKalenderRepository). De velden staan
    // hier alleen voor signatuurgelijkheid; blijven altijd null/false.
    bool? KnvbPdfBijlageIngeschakeld = null,
    string? KnvbStandaardRegio = null);
