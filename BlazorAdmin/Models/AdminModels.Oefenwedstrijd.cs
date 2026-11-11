namespace BlazorAdmin.Models;

/// <summary>#1116: respons van POST /api/sportlink/club-match — het generieke mutatieresultaat plus
/// wat de server uit teamnaam en instellingen heeft afgeleid (Sportlink-team-ID, leeftijdscategorie,
/// locatie-ID, veld), zodat de beheerder ziet wat er (gesimuleerd) naar Sportlink zou gaan. Spiegelt
/// FunctionApp.Postgres.Sportlink.SportlinkClubMatchFunction.OefenwedstrijdAanmaakResultaat.</summary>
public class OefenwedstrijdResultaatDto : SportlinkMutatieResultaatDto
{
    public string? Omschrijving { get; set; }
    public string? SportlinkTeamId { get; set; }
    public string? AgeClassCode { get; set; }
    public string? FacilityId { get; set; }
    /// <summary>#1427: het veld zoals Sportlink het kent (bijv. "…-OUTDOOR_FIELD-6").</summary>
    public string? SubFacilityId { get; set; }
    /// <summary>#1427: spelactiviteit, bijv. "SOCCER-VE-AL/FRIDAY".</summary>
    public string? SportIdTag { get; set; }
    /// <summary>#1437: wedstrijdnummer uit de eigen teller — YYMMDD + volgnummer per speeldag (bijv. 26100201).</summary>
    public long? WedstrijdNummer { get; set; }
    public string? VeldNaam { get; set; }
    public List<string> Waarschuwingen { get; set; } = new();
    /// <summary>#1437: het velddeel zoals het naar Sportlink ging ("1.0", "0.5", "0.25" of "0.125").</summary>
    public string? Velddeel { get; set; }
}

/// <summary>#1437: respons van <c>GET /api/sportlink/club-match/formulier</c> — de voorinvulling voor "Wedstrijd aanmaken".
/// <see cref="SportlinkBeschikbaar"/> is <c>false</c> als Sportlinks leeftijdscategorielijst niet kon worden opgehaald.</summary>
public class OefenwedstrijdFormulierDto
{
    public List<OefenwedstrijdFormulierTeamDto> Teams { get; set; } = new();
    public List<OefenwedstrijdAgeClassDto> AgeClasses { get; set; } = new();
    public bool SportlinkBeschikbaar { get; set; }
}

/// <summary>#1437: één actief team met wat het formulier ervoor voorinvult. <see cref="Veldafmeting"/> is de velddeel-waarde
/// ("1.0", "0.5", "0.25", "0.125"), <c>null</c> als de speeltijden hem niet kennen.</summary>
public class OefenwedstrijdFormulierTeamDto
{
    public string TeamNaam { get; set; } = "";
    public string? Leeftijdscategorie { get; set; }
    public string? AgeClassCode { get; set; }
    public int? Duur { get; set; }
    public string? Veldafmeting { get; set; }
}

/// <summary>#1437: één leeftijdscategorie uit Sportlinks lijst.</summary>
public class OefenwedstrijdAgeClassDto
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
}
