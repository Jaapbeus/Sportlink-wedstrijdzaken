namespace BlazorAdmin.Models;

/// <summary>#988: rol↔serviceaccount-koppelingsstatus, zie docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §6.</summary>
public class SportlinkExtensieRolDto
{
    public string RolNaam { get; set; } = "";
    public bool Gekoppeld { get; set; }
    public string? LaatstGekoppeldDoor { get; set; }
    public DateTime? LaatstGekoppeldOp { get; set; }
    public string? SportlinkAccountNaam { get; set; }
}

/// <summary>Niet-geheime status van automatische Sportlink-login voor een functionele rol.</summary>
public class SportlinkAutoLoginStatusDto
{
    public bool Configured { get; set; }
    public bool Enabled { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public DateTime? RetryAfterUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Write-only Sportlink-inloggegevens; deze waarden worden nooit opgehaald of lokaal bewaard.</summary>
public class SportlinkAutoLoginRequestDto
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string TotpSecret { get; set; } = "";
    public string TotpAlgorithm { get; set; } = "SHA1";
    public int TotpDigits { get; set; } = 6;
    public int TotpPeriodSeconds { get; set; } = 30;
}

/// <summary>#1427: respons van <c>GET /api/sportlink/club-match/dryrun-status</c>.</summary>
public class SportlinkDryRunStatusDto
{
    public bool DryRun { get; set; }
}

/// <summary>#1773: respons van GET /api/sportlink/koppeling-status — niveau <c>Ok</c>/<c>Waarschuwing</c>/
/// <c>Verbroken</c> plus de melding voor de gebruiker; nooit een tokenwaarde. Spiegelt
/// Planner.Shared.Integrations.SportlinkClub.SportlinkKoppelingStatus.</summary>
public class SportlinkKoppelingStatusDto
{
    public string RolNaam { get; set; } = "";
    public string Niveau { get; set; } = "Ok";
    public bool IsVerbroken { get; set; }
    public string? Melding { get; set; }
    public DateTime? LaatsteLoginUtc { get; set; }
}

/// <summary>#998: status van de Sportlink Web Extension voor de Instellingen-pagina — nooit een
/// tokenwaarde of Match-data met persoonsgegevens.</summary>
public class SportlinkExtensieHealthDto
{
    public bool ExtensionEnabled { get; set; }
    public bool DryRun { get; set; }
    public bool EgressAllowed { get; set; }
    public List<SportlinkExtensieHealthRolDto> Rollen { get; set; } = new();
    public string? LaatsteMutatieFout { get; set; }
    public DateTime? LaatsteMutatieFoutOp { get; set; }
    public SportlinkContractCheckDto? LaatsteContractCheck { get; set; }
    public SportlinkExtensieHealthLiveDto? Live { get; set; }
}

public class SportlinkExtensieHealthRolDto
{
    public string RolNaam { get; set; } = "";
    public bool Gekoppeld { get; set; }
    public DateTime? LaatstVerverstOp { get; set; }
    public DateTime? RefreshTokenVervaltOp { get; set; }
}

public class SportlinkContractCheckDto
{
    public DateTime UitgevoerdOp { get; set; }
    public bool IsOk { get; set; }
    public int? HttpStatus { get; set; }
    public string? AfwijkendeVelden { get; set; }
    public string? FoutmeldingSamenvatting { get; set; }
}

/// <summary>Alleen gevuld als de gebruiker expliciet op "Nu live controleren" heeft geklikt
/// (<c>?live=true</c>) — bevat uitsluitend een HTTP-status, nooit responsdata.</summary>
public class SportlinkExtensieHealthLiveDto
{
    public bool TokenRefreshGelukt { get; set; }
    public string? MatchCheckResultaat { get; set; }
    public int? MatchCheckHttpStatus { get; set; }
}
