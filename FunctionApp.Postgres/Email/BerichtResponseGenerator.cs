using Planner.Shared.Email;
using FunctionApp.Postgres.Planner;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Tier-omhulsel van <see cref="BerichtAntwoordGenerator"/> (#1758, slice 6 van epic #1716). De antwoordteksten
/// staan in Planner.Shared; dit omhulsel geeft de DTO's van deze tier door (die de poorten zelf implementeren) en
/// kiest de instellingenbron: de club-snapshot van de e-mailtester, of zonder snapshot de procesbrede
/// <see cref="PostgresAppSettings"/>-cache (#677).
/// </summary>
public static class BerichtResponseGenerator
{
    internal const string NlTijdzoneId = "W. Europe Standard Time";

    public static (string onderwerp, string body) BouwBeschikbaarheidAntwoord(
        CheckAvailabilityResponse response, BerichtClassificatie classificatie, InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null)
        => BerichtAntwoordGenerator.BouwBeschikbaarheidAntwoord(response, classificatie, email, Instellingen(clubSettings));

    public static (string onderwerp, string body) BouwMultiDatumBeschikbaarheidAntwoord(
        List<(string datum, CheckAvailabilityResponse response)> resultaten, BerichtClassificatie classificatie,
        InkomendBericht email, ClubAppSettingsSnapshot? clubSettings = null)
        => BerichtAntwoordGenerator.BouwMultiDatumBeschikbaarheidAntwoord(
            resultaten.Select(r => (r.datum, (IAntwoordBeschikbaarheid)r.response)).ToList(),
            classificatie, email, Instellingen(clubSettings));

    public static (string onderwerp, string body) BouwHerplanAntwoord(
        ZoekWedstrijdResponse? wedstrijd, HerplanCheckResponse? herplanOpties, BerichtClassificatie classificatie,
        InkomendBericht email, ClubAppSettingsSnapshot? clubSettings = null)
        => BerichtAntwoordGenerator.BouwHerplanAntwoord(wedstrijd, herplanOpties, classificatie, email, Instellingen(clubSettings));

    public static (string onderwerp, string body) BouwHerplanTeLaatAntwoord(
        ZoekWedstrijdResponse? wedstrijd, int deadlineDagen, int dagenTotWedstrijd, BerichtClassificatie classificatie,
        InkomendBericht email, ClubAppSettingsSnapshot? clubSettings = null)
        => BerichtAntwoordGenerator.BouwHerplanTeLaatAntwoord(
            wedstrijd, deadlineDagen, dagenTotWedstrijd, classificatie, email, Instellingen(clubSettings));

    public static (string onderwerp, string body) BouwVerzetZonderDatumAntwoord(
        ZoekWedstrijdResponse? wedstrijd, List<string> vrijeZaterdagen, BerichtClassificatie classificatie,
        InkomendBericht email, ClubAppSettingsSnapshot? clubSettings = null)
        => BerichtAntwoordGenerator.BouwVerzetZonderDatumAntwoord(
            wedstrijd, vrijeZaterdagen, classificatie, email, Instellingen(clubSettings));

    public static (string onderwerp, string body) BouwHerplanGewensteDatumAntwoord(
        ZoekWedstrijdResponse? wedstrijd, string? gewensteDatum, CheckAvailabilityResponse? beschikbaarheid,
        BerichtClassificatie classificatie, InkomendBericht email, ClubAppSettingsSnapshot? clubSettings = null)
        => BerichtAntwoordGenerator.BouwHerplanGewensteDatumAntwoord(
            wedstrijd, gewensteDatum, beschikbaarheid, classificatie, email, Instellingen(clubSettings));

    public static (string onderwerp, string body) BouwTeamContactAutoReply(
        BerichtClassificatie classificatie, InkomendBericht email, ClubAppSettingsSnapshot? clubSettings = null)
        => BerichtAntwoordGenerator.BouwTeamContactAutoReply(classificatie, email, Instellingen(clubSettings));

    public static (string onderwerp, string body) BouwBevestigingAntwoord(
        InkomendBericht email, BerichtClassificatie classificatie, ClubAppSettingsSnapshot? clubSettings = null)
        => BerichtAntwoordGenerator.BouwBevestigingAntwoord(email, classificatie, Instellingen(clubSettings));

    public static (string onderwerp, string body) BouwBuitenScopeAntwoord(
        InkomendBericht email, ClubAppSettingsSnapshot? clubSettings = null)
        => BerichtAntwoordGenerator.BouwBuitenScopeAntwoord(email, Instellingen(clubSettings));

    public static (string onderwerp, string body) BouwWedstrijdAlIngeplandAntwoord(
        ZoekWedstrijdResponse? wedstrijd, BerichtClassificatie classificatie, InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null)
        => BerichtAntwoordGenerator.BouwWedstrijdAlIngeplandAntwoord(wedstrijd, classificatie, email, Instellingen(clubSettings));

    public static (string onderwerp, string body) BouwTeamOnbekendAntwoord(
        string tegenstander, BerichtClassificatie classificatie, InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null)
        => BerichtAntwoordGenerator.BouwTeamOnbekendAntwoord(tegenstander, classificatie, email, Instellingen(clubSettings));

    public static (string onderwerp, string body) BouwFoutAntwoord(
        InkomendBericht email, BerichtClassificatie classificatie, ClubAppSettingsSnapshot? clubSettings = null)
        => BerichtAntwoordGenerator.BouwFoutAntwoord(email, classificatie, Instellingen(clubSettings));

    public static (string onderwerp, string body) BouwAangepasteAntwoord(
        EmailTemplate template, BerichtClassificatie classificatie, InkomendBericht email,
        ClubAppSettingsSnapshot? clubSettings = null, IDictionary<string, string>? extraPlaceholders = null,
        string? gecontroleerdDagdeel = null)
        => BerichtAntwoordGenerator.BouwAangepasteAntwoord(
            template, classificatie, email, Instellingen(clubSettings), extraPlaceholders, gecontroleerdDagdeel);

    /// <summary>De instellingenbron van deze tier; zie <see cref="AntwoordInstellingen"/>.</summary>
    internal static IAntwoordInstellingen Instellingen(ClubAppSettingsSnapshot? clubSettings) => new AntwoordInstellingen(clubSettings);

    /// <summary>
    /// Leest per veld uit de club-snapshot (dry-run pad van de e-mailtester) wanneer die er is, anders uit de
    /// procesbrede cache (de echte e-mailpipeline, één club). Binnen een snapshot valt een ontbrekend veld NIET
    /// terug op de globale cache: dat zou de echte productieclub laten lekken in een democlub-dry-run (#677).
    /// Elk veld wordt pas gelezen als de gedeelde generator het nodig heeft, net als voorheen.
    /// </summary>
    private sealed class AntwoordInstellingen(ClubAppSettingsSnapshot? club) : IAntwoordInstellingen
    {
        public string? EmailVoetnoot => club != null ? club.EmailVoetnoot : PostgresAppSettings.GetSetting("emailVoetnoot");
        public string? PlannerAfzenderNaam => club != null ? club.PlannerAfzenderNaam : PostgresAppSettings.GetSetting("plannerAfzenderNaam");
        public string? CoordinatorNaam => club != null ? club.CoordinatorNaam : PostgresAppSettings.GetSetting("coordinatorNaam");
        public string? CoordinatorFunctie => club != null ? club.CoordinatorFunctie : PostgresAppSettings.GetSetting("coordinatorFunctie");
        public string OntbrekendeAfzenderNaamMelding => club != null
            ? "Vereiste instelling 'PlannerAfzenderNaam' ontbreekt in public.appsettings voor de opgegeven club"
            : "Vereiste instelling 'plannerAfzenderNaam' ontbreekt in public.appsettings";
    }
}
