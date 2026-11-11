namespace BlazorAdmin.Models;

public class VoorkeurTijdDto
{
    public int Id { get; set; }
    public string TeamNaam { get; set; } = "";
    public int DagVanWeek { get; set; }
    public string VoorkeurTijd { get; set; } = "";
    public int Prioriteit { get; set; } = 5;
    public bool Actief { get; set; } = true;
    public string? ClubCode { get; set; }
}

public class TeamRegelDto
{
    public int Id { get; set; }
    public string TeamNaam { get; set; } = "";
    public string RegelType { get; set; } = "";
    public int? WaardeMinuten { get; set; }
    public int? WaardeVeldNummer { get; set; }
    public string? WaardeTijd { get; set; }
    public int Prioriteit { get; set; }
    public bool Actief { get; set; }
    public string? Opmerking { get; set; }
    public string ClubCode { get; set; } = string.Empty;
}

// ── Teambegeleiding ──

public class TeambegeldingImportRequest
{
    public string CsvContent { get; set; } = "";
    public string? Bestandsnaam { get; set; }
}

public class TeambegeldingImportResultaat
{
    public int Rijen { get; set; }
    public List<string> Herkend { get; set; } = [];
    public List<string> Ontbreekt { get; set; } = [];
    public List<string> Waarschuwingen { get; set; } = [];
}

public class TeambegeleidingItem
{
    public string Naam { get; set; } = "";
    public string Teamrol { get; set; } = "";
    public string? Functie { get; set; }
    public string? Emailadres { get; set; }
    public string? Telefoonnummer { get; set; }

    /// <summary>Badge-tekst: "Teamrol - Functie", of alleen de Teamrol als er geen Functie is (#1360).</summary>
    public string RolLabel => string.IsNullOrWhiteSpace(Functie) ? Teamrol : $"{Teamrol} - {Functie}";
}

public class DoorsturenRequest
{
    public string TeamNaam { get; set; } = "";
    public string? Onderwerp { get; set; }
    public string Bericht { get; set; } = "";
    public string? Ontvangers { get; set; }
}

// ── Speeltijden ──

public class SpeeltijdDto
{
    public string Leeftijd { get; set; } = "";
    public decimal Veldafmeting { get; set; } = 1.00m;
    public int WedstrijdTotaal { get; set; }
    public int WedstrijdHelft { get; set; }
    public int WedstrijdRust { get; set; }

    /// <summary>
    /// Standaard voorkeurstijd "HH:mm" voor deze leeftijdscategorie (#666). Leeg = geen streeftijd;
    /// de planner gebruikt deze tijd voor teams zonder eigen rij in de voorkeurstijden.
    /// </summary>
    public string? StandaardVoorkeurTijd { get; set; }
}
