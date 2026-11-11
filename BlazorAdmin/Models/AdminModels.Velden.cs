namespace BlazorAdmin.Models;

// Velden (#679: aanvulling met VeldType/HeeftKunstlicht/Actief voor Admin-CRUD)
public class VeldDto
{
    public int    VeldNummer      { get; set; }
    public string VeldNaam        { get; set; } = "";
    public string VeldType        { get; set; } = "kunstgras";
    public bool   HeeftKunstlicht { get; set; }
    public bool   Actief          { get; set; } = true;
}

// VeldBeschikbaarheid (#679: eerste GUI voor de al langer bestaande API)
public class VeldBeschikbaarheidDto
{
    public int    Id                   { get; set; }
    public int    VeldNummer           { get; set; }
    public string VeldNaam             { get; set; } = "";
    public int    DagVanWeek           { get; set; }
    public string BeschikbaarVanaf     { get; set; } = "";
    public string BeschikbaarTot       { get; set; } = "";
    public bool   GebruikZonsondergang { get; set; }
    // #581: NULL = standaardregime (geldt buiten elke actieve periode, zoals vóór deze feature).
    public int?    PeriodeId           { get; set; }
    public string? PeriodeNaam         { get; set; }
}

// VeldPeriode (#581: herbruikbaar regime, bijv. "Zomerstop" of "Competitie", met een geldigheidsrange)
public class VeldPeriodeDto
{
    public int    Id       { get; set; }
    public string Naam     { get; set; } = "";
    public string DatumVan { get; set; } = "";
    public string DatumTot { get; set; } = "";
    public bool   Actief   { get; set; } = true;
}

// VeldTraining (#679: trainingsschema per veld per weekdag)
public class VeldTrainingDto
{
    public int     Id            { get; set; }
    public int     VeldNummer    { get; set; }
    public string  VeldNaam      { get; set; } = "";
    public int     DagVanWeek    { get; set; }
    public string  VanTijd       { get; set; } = "";
    public string  TotTijd       { get; set; } = "";
    public string? Omschrijving  { get; set; }
    public bool    Actief        { get; set; } = true;
}
