namespace BlazorAdmin.Models;

// Thema (#325, #339)
public class ThemeDto
{
    public string Primary       { get; set; } = "#1b6ec2";
    public string Secondary     { get; set; } = "#6c757d";
    public string Accent        { get; set; } = "#0071c1";
    public string TextOnPrimary { get; set; } = "#ffffff";
    public string? ClubWebsiteUrl { get; set; }
    public string? FaviconUrl   { get; set; }
    public string? LogoUrl      { get; set; }

    // #1254 (epic #1249): volledig kleurenpalet per modus, sleutel -> #rrggbb of #rrggbbaa.
    // Bewust een dictionary en geen veld per kleur — het aantal kleuren groeit nog. null of leeg
    // betekent: niet ingesteld, val terug op de vier platte velden hierboven.
    public Dictionary<string, string>? LightColors { get; set; }
    public Dictionary<string, string>? DarkColors  { get; set; }
}

public class ThemeExtractResultDto
{
    public List<string> Colors    { get; set; } = new();
    public string?      FaviconUrl { get; set; }
    public string?      LogoUrl    { get; set; }
}
