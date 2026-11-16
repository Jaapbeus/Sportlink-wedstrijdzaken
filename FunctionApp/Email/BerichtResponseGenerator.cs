using Planner.Shared.Email;
using SportlinkFunction.Planner;

namespace SportlinkFunction.Email;

/// <summary>
/// Tier-omhulsel van <see cref="BerichtAntwoordGenerator"/> (#1758, slice 6 van epic #1716). De antwoordteksten
/// staan in Planner.Shared; dit omhulsel geeft de DTO's van deze tier door (die de poorten zelf implementeren) en
/// kiest de instellingenbron: de club-snapshot van de e-mailtester, of zonder snapshot de procesbrede
/// <see cref="SystemUtilities.AppSettings"/>-cache (#677).
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

    /// <summary>De instellingenbron van deze tier; de snapshot blijft leidend.</summary>
    internal static IAntwoordInstellingen Instellingen(ClubAppSettingsSnapshot? clubSettings) =>
        new SnapshotAntwoordInstellingen(clubSettings, SystemUtilities.AppSettings.GetSetting,
            "Vereiste instelling 'PlannerAfzenderNaam' ontbreekt in dbo.AppSettings voor de opgegeven club",
            "Vereiste instelling 'plannerAfzenderNaam' ontbreekt in dbo.AppSettings");
}
