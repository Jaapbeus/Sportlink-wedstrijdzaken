namespace BlazorAdmin.Models;

// Test data / ALLSTARS (#365)
public class AllstarsWedstrijdDto
{
    public string  BkMatches      { get; set; } = "";
    public string? Datum          { get; set; }
    public string? Aanvangstijd   { get; set; }
    public string? ThuisTeam      { get; set; }
    public string? UitTeam        { get; set; }
    public string? VeldNaam       { get; set; }
    public string? VeldSubpositie { get; set; }
    public string? Soort          { get; set; }
}

public class AllstarsVerplaatsDatumResultaat
{
    public bool Ok               { get; set; }
    public int  AantalVerplaatst { get; set; }
}
