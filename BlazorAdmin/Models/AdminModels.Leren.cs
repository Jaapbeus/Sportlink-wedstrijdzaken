namespace BlazorAdmin.Models;

// ── Leermomenten (#323) ──

public class LeermomentDto
{
    public int Id { get; set; }
    /// <summary>Alleen bij herkomst Reply; een admin-leermoment (#1568 deel C) heeft geen reply-paar.</summary>
    public int? OrigineleVerwerkingId { get; set; }
    public int? CorrectionVerwerkingId { get; set; }
    public string OrigineelVerzoekType { get; set; } = "";
    public string? AfgeleidJuistType { get; set; }
    public string? OrigineleSamenvatting { get; set; }
    public string? CorrectieSamenvatting { get; set; }
    public bool IsGevalideerd { get; set; }
    public bool IsAfgewezen { get; set; }
    /// <summary><c>Reply</c> (AI-herkende reply-correctie) of <c>Admin</c> (door een beheerder, permanent).</summary>
    public string Herkomst { get; set; } = "Reply";
    /// <summary>Weergavenaam (momentopname) van de beheerder; <c>null</c> bij herkomst Reply.</summary>
    public string? AangemaaktDoorNaam { get; set; }
    public DateTime? AangemaaktOp { get; set; }
    public int? HerkomstVerwerkingId { get; set; }
    public DateTime MtaInserted { get; set; }
}

/// <summary>Body van <c>POST /api/beheer/leermomenten</c> (#1568 deel C). De aanmaker bepaalt de server.</summary>
public class LeermomentAanmaakDto
{
    public string? OrigineelVerzoekType { get; set; }
    public string JuistVerzoekType { get; set; } = "";
    public string Samenvatting { get; set; } = "";
    public int? HerkomstVerwerkingId { get; set; }
}

public class LeermomentenResponse
{
    public int Count { get; set; }
    public int Limit { get; set; }
    public List<LeermomentDto> Items { get; set; } = new();
}

public class LeermomentenStatsDto
{
    public int Pending { get; set; }
    public int Validated { get; set; }
    public int Rejected { get; set; }
}

// ── Teamaliassen (#701) ──

public class TeamAliasDto
{
    public int Id { get; set; }
    public string RuweTekst { get; set; } = "";
    public string RuweTekstGenormaliseerd { get; set; } = "";
    public int TeamId { get; set; }
    /// <summary>Canonieke teamnaam uit dbo.Teams; null als het team inmiddels is verwijderd.</summary>
    public string? Teamnaam { get; set; }
    public string? LeeftijdsCategorie { get; set; }
    public string Bron { get; set; } = "";
    public string Status { get; set; } = "";
    public int AantalKeerGebruikt { get; set; }
    /// <summary>UTC uit de database — altijd .ToLocalTime() vóór weergave.</summary>
    public DateTime? MtaInserted { get; set; }
    public DateTime? MtaModified { get; set; }
    // #1568 deel C: wie/wanneer. Alleen de weergavenaam (momentopname); null = door het systeem (sync/AI).
    public string? AangemaaktDoorNaam { get; set; }
    public DateTime? AangemaaktOp { get; set; }
    public int? HerkomstVerwerkingId { get; set; }
    public string? Reden { get; set; }
    public string? BeoordeeldDoorNaam { get; set; }
    public DateTime? BeoordeeldOp { get; set; }
}

/// <summary>Een team uit <c>GET /api/beheer/teams/keuzelijst</c> (#1568 deel C).</summary>
public class TeamKeuzeDto
{
    public int TeamId { get; set; }
    public string Teamnaam { get; set; } = "";
    public string? LeeftijdsCategorie { get; set; }
}

/// <summary>Body van <c>POST /api/beheer/teamaliassen</c> (#1568 deel C). De aanmaker bepaalt de server.</summary>
public class TeamAliasAanmaakDto
{
    public string RuweTekst { get; set; } = "";
    public int TeamId { get; set; }
    /// <summary>Alleen <c>true</c> na een expliciete bevestiging van de beheerder (anders volgt bij een bestaande alias een 409).</summary>
    public bool Herkoppel { get; set; }
    /// <summary>Alleen <c>true</c> na een expliciete bevestiging dat de tekst bij meerdere teams past (409 met code <c>dubbelzinnig</c>).</summary>
    public bool BevestigDubbelzinnig { get; set; }
    public int? HerkomstVerwerkingId { get; set; }
    public string? Reden { get; set; }
}

public class TeamAliasAanmaakResultaatDto
{
    public int Id { get; set; }
    /// <summary><c>aangemaakt</c>, <c>herkoppeld</c> of <c>bestaat-al</c>.</summary>
    public string Status { get; set; } = "";
    public string RuweTekst { get; set; } = "";
    public string Teamnaam { get; set; } = "";
}

/// <summary>Een regel uit de wachtrij met onbekende teamteksten (#1568 deel C).</summary>
public class OnbekendeTeamTekstDto
{
    public int Id { get; set; }
    public string Voorbeeld { get; set; } = "";
    public string Genormaliseerd { get; set; } = "";
    public int Aantal { get; set; }
    public DateTime EerstGezien { get; set; }
    public DateTime LaatstGezien { get; set; }
    public int? LaatsteVerwerkingId { get; set; }
    public string Status { get; set; } = "open";
}

public class OnbekendeTeamTekstenResponse
{
    public int Count { get; set; }
    public int Open { get; set; }
    public List<OnbekendeTeamTekstDto> Items { get; set; } = new();
}

/// <summary>
/// Antwoord van <c>POST /api/beheer/teams/herstel</c> (#946). De voor- en na-tellingen staan er
/// bewust allebei in: "hersteld" zonder te laten zien wat er veranderde, is voor een beheerder niet
/// te onderscheiden van "er gebeurde niets".
/// </summary>
public class TeamHerstelDto
{
    public string ClubCode { get; set; } = string.Empty;
    public int TeamsVoor { get; set; }
    public int TeamsNa { get; set; }
    public int AliassenNa { get; set; }
    public int SleutelsGemigreerd { get; set; }
    public int DubbelenOpgeruimd { get; set; }
}

public class TeamAliassenResponse
{
    public int Count { get; set; }
    public int Limit { get; set; }
    public int Pending { get; set; }
    public int Validated { get; set; }
    public int Rejected { get; set; }
    public List<TeamAliasDto> Items { get; set; } = new();
}
