namespace BlazorAdmin.Models;

public class TemplateDto
{
    public int Id { get; set; }
    public string TemplateKey { get; set; } = "";
    public string Onderwerp { get; set; } = "";
    public string BodyTemplate { get; set; } = "";
    public bool Actief { get; set; }
    public string ClubCode { get; set; } = "";
}

public class EmailLogDto
{
    public int Id { get; set; }
    public string? MessageId { get; set; }
    public string? Afzender { get; set; }
    public DateTime OntvangstDatum { get; set; }
    public string? VerzoekType { get; set; }
    public string? Status { get; set; }
    public string? VerstuurdNaar { get; set; }
    public string? FoutMelding { get; set; }
    /// <summary>Er is een beslissingstrace opgeslagen voor dit bericht (#1568).</summary>
    public bool HeeftTrace { get; set; }
}

/// <summary>Opgeslagen beslissingstrace van één verwerking (#1568); PII-arm, zonder body/afzender/onderwerp.</summary>
public class EmailTraceDto
{
    public int VerwerkingId { get; set; }
    public string VerzoekType { get; set; } = "";
    /// <summary>Status van de verwerking; <c>null</c> als die inmiddels is opgeruimd.</summary>
    public string? Status { get; set; }
    public DateTime? OntvangstDatum { get; set; }
    public DateTime Aangemaakt { get; set; }
    public string Zekerheid { get; set; } = "";
    public string? SjabloonSleutel { get; set; }
    public string AppVersie { get; set; } = "";
    public BeslissingsTraceDto? Trace { get; set; }
}

public class EmailLogResponse
{
    public int Count { get; set; }
    public int Limit { get; set; }
    public Dictionary<string, int>? StatusCounts { get; set; }
    public List<EmailLogDto> Items { get; set; } = new();
}

public class TestEmailRequest
{
    public string? Onderwerp { get; set; }
    public string? Afzender { get; set; }
    public string? AfzenderNaam { get; set; }
    public string? Body { get; set; }
}

public class TestEmailResponse
{
    public bool DryRun { get; set; }
    public string? Opmerking { get; set; }
    public object? Classificatie { get; set; }
    public object? PlannerResponse { get; set; }
    public VoorbeeldAntwoord? VoorbeeldAntwoord { get; set; }
    /// <summary>Beslissingstrace van de pipeline (#1568); <c>null</c> bij een oudere server.</summary>
    public BeslissingsTraceDto? Trace { get; set; }
    /// <summary>
    /// Definitief eindoordeel met de actuele zekerheidspoort-instelling meegewogen (#1583); <c>null</c> bij een
    /// oudere server.
    /// </summary>
    public TesterEindoordeelDto? Eindoordeel { get; set; }
    /// <summary>Voorzet voor "Verzoektype corrigeren" (#1568 deel C): gesaneerde samenvatting van de classificatie.</summary>
    public LeersuggestieDto? Leersuggestie { get; set; }
}

/// <summary>Spiegel van <c>Planner.Shared.Email.Trace.TesterEindoordeel</c>; Uitkomst als tekst (AutomatischVerstuurd/Review/GeenAntwoord).</summary>
public class TesterEindoordeelDto
{
    public string Uitkomst { get; set; } = "";
    public string Titel { get; set; } = "";
    public string Toelichting { get; set; } = "";
    public bool Waarschuwing { get; set; }
    public string ConceptLabel { get; set; } = "";
    public bool ZekerheidspoortActief { get; set; }
    public bool ReviewModusActief { get; set; }
}

public class LeersuggestieDto
{
    public string VerzoekType { get; set; } = "";
    public string Samenvatting { get; set; } = "";
}

/// <summary>Spiegel van <c>Planner.Shared.Email.Trace.BeslissingsTrace</c>; Zekerheid als tekst (Zeker/Onzeker/Mislukt).</summary>
public class BeslissingsTraceDto
{
    public List<TraceStapDto> Stappen { get; set; } = new();
    public TraceOordeelDto? Oordeel { get; set; }
}

public class TraceStapDto
{
    public string Code { get; set; } = "";
    public string Titel { get; set; } = "";
    public string Uitkomst { get; set; } = "";
    public string Zekerheid { get; set; } = "Zeker";
    public Dictionary<string, string> Details { get; set; } = new();
}

public class TraceOordeelDto
{
    public bool IsZeker { get; set; }
    public List<string> Redenen { get; set; } = new();
}

public class VoorbeeldAntwoord
{
    public string? Onderwerp { get; set; }
    public string? Body { get; set; }
}

public class UitgeslotenEmailAdresDto
{
    public int Id { get; set; }
    public string EmailAdres { get; set; } = "";
    public string? Omschrijving { get; set; }
    public bool Actief { get; set; } = true;
    public string ClubCode { get; set; } = "";
}
