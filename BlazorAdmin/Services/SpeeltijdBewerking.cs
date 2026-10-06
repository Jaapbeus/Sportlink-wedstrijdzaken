using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Eén geopend formulier op de Speeltijden-pagina. Iedere klik op Bewerken of Nieuwe categorie maakt
/// een nieuwe sessie — ook voor dezelfde categorie — zodat een laat binnenkomend opslagresultaat
/// herkend wordt aan de sessie zelf en niet aan de categorienaam (#1552).
/// </summary>
public sealed class SpeeltijdBewerkSessie
{
    internal SpeeltijdBewerkSessie(SpeeltijdDto model, bool isNieuw)
    {
        Model = model;
        IsNieuw = isNieuw;
    }

    public SpeeltijdDto Model { get; }
    public bool IsNieuw { get; }
    public string? Fout { get; internal set; }
    public bool Bezig { get; internal set; }

    /// <summary>De categorienaam zoals de gebruiker hem bij het openen zag.</summary>
    public string Naam => IsNieuw
        ? (string.IsNullOrWhiteSpace(Model.Leeftijd) ? "de nieuwe categorie" : Model.Leeftijd)
        : Model.Leeftijd;
}

/// <summary>
/// Bewerkstatus van de Speeltijden-pagina (#1543, #1552). Een afgeronde opslagactie verandert
/// uitsluitend de sessie die hem startte: is die inmiddels gesloten of vervangen, dan sluit hij
/// het huidige formulier niet en toont hij zijn fout niet onder een andere regel, maar als
/// <see cref="Melding"/> met de naam van de categorie waar hij bij hoort.
/// </summary>
public sealed class SpeeltijdBewerking
{
    private readonly List<SpeeltijdBewerkSessie> _lopend = new();

    public SpeeltijdBewerkSessie? Actief { get; private set; }

    /// <summary>Fout van een opslagactie waarvan het formulier al gesloten of vervangen is.</summary>
    public string? Melding { get; private set; }

    public void StartNieuw() => Actief = new SpeeltijdBewerkSessie(new SpeeltijdDto(), isNieuw: true);

    /// <summary>
    /// Opent het formulier voor een bestaande regel. Weigert zolang een eerdere opslag van diezelfde
    /// categorie nog loopt: de kopie zou dan de waarden van vóór die opslag bevatten en bij Opslaan het
    /// zojuist opgeslagen resultaat weer overschrijven. De pagina toont die regel intussen als bezig.
    /// </summary>
    public bool StartBewerken(SpeeltijdDto bron)
    {
        if (IsOpslagBezig(bron)) return false;
        Actief = new SpeeltijdBewerkSessie(new SpeeltijdDto
        {
            Leeftijd = bron.Leeftijd,
            Veldafmeting = bron.Veldafmeting,
            WedstrijdTotaal = bron.WedstrijdTotaal,
            WedstrijdHelft = bron.WedstrijdHelft,
            WedstrijdRust = bron.WedstrijdRust,
            StandaardVoorkeurTijd = bron.StandaardVoorkeurTijd
        }, isNieuw: false);
        return true;
    }

    public void Annuleer() => Actief = null;

    public void SluitMelding() => Melding = null;

    /// <summary>Het bewerkformulier staat direct onder deze regel (#1543).</summary>
    public bool IsInBewerking(SpeeltijdDto regel) =>
        Actief is { IsNieuw: false } sessie && SleutelGelijk(sessie, regel);

    /// <summary>Een opslag van deze regel is onderweg — ook als het formulier intussen gesloten of vervangen is.</summary>
    public bool IsOpslagBezig(SpeeltijdDto regel) =>
        _lopend.Any(sessie => !sessie.IsNieuw && SleutelGelijk(sessie, regel));

    /// <summary>
    /// Slaat de actieve sessie op. Geeft <c>true</c> terug als er iets is opgeslagen, zodat de pagina
    /// de lijst ververst — ook als het formulier intussen gesloten of vervangen is.
    /// </summary>
    public async Task<bool> OpslaanAsync(Func<SpeeltijdDto, bool, Task<ApiResult<object>>> opslaan)
    {
        var sessie = Actief;
        if (sessie is null || sessie.Bezig) return false;

        sessie.Bezig = true;
        sessie.Fout = null;
        _lopend.Add(sessie);
        ApiResult<object> resultaat;
        try
        {
            resultaat = await opslaan(sessie.Model, sessie.IsNieuw);
        }
        finally
        {
            sessie.Bezig = false;
            _lopend.Remove(sessie);
        }

        var nogActief = ReferenceEquals(Actief, sessie);
        if (resultaat.Success)
        {
            if (nogActief) Actief = null;
            return true;
        }

        var fout = resultaat.ErrorMessage ?? "Opslaan mislukt";
        if (nogActief)
            sessie.Fout = fout;
        else
            Melding = $"Opslaan van {sessie.Naam} is mislukt: {fout}";
        return false;
    }

    private static bool SleutelGelijk(SpeeltijdBewerkSessie sessie, SpeeltijdDto regel) =>
        string.Equals(sessie.Model.Leeftijd, regel.Leeftijd, StringComparison.Ordinal);
}
