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
/// Lijst- en bewerkstatus van de Speeltijden-pagina (#1543, #1552). Een afgeronde opslagactie verandert
/// uitsluitend de sessie die hem startte: is die inmiddels gesloten of vervangen, dan sluit hij
/// het huidige formulier niet en toont hij zijn fout niet onder een andere regel, maar als
/// <see cref="Melding"/> met de naam van de categorie waar hij bij hoort.
/// </summary>
public sealed class SpeeltijdBewerking
{
    private readonly List<SpeeltijdBewerkSessie> _lopend = new();
    private List<SpeeltijdDto> _items = new();

    /// <summary>
    /// Volgnummer van de laatst aangevraagde lijstverversing. Meerdere regels mogen tegelijk worden
    /// opgeslagen, en elke opslag vraagt daarna de lijst op; die antwoorden kunnen in omgekeerde
    /// volgorde binnenkomen. Alleen het antwoord op de laatst gestelde vraag wordt toegepast — een
    /// ouder antwoord zou een intussen bijgewerkte, al vrijgegeven regel terugzetten (PR #1557, ronde 2).
    /// </summary>
    private int _laadGeneratie;

    public IReadOnlyList<SpeeltijdDto> Items => _items;

    public SpeeltijdBewerkSessie? Actief { get; private set; }

    /// <summary>Fout van een opslagactie waarvan het formulier al gesloten of vervangen is.</summary>
    public string? Melding { get; private set; }

    /// <summary>
    /// De lijst kon na een geslaagde opslag niet opnieuw worden opgehaald. De opgeslagen regel is dan
    /// lokaal bijgewerkt, zodat de tabel en een heropend formulier toch de opgeslagen waarden tonen.
    /// </summary>
    public string? VerversFout { get; private set; }

    /// <summary>
    /// Gaat af zodra de lijst tussentijds verandert — na de lokale bijwerking van een opgeslagen regel,
    /// vóór de verversing. Blazor rendert een eventhandler pas na zijn laatste <c>await</c>; zonder dit
    /// signaal zou de opgeslagen waarde pas zichtbaar worden als de verse lijst er is.
    /// </summary>
    public event Action? Gewijzigd;

    /// <summary>
    /// Haalt de lijst op. Het resultaat gaat terug naar de pagina voor de laadfoutafhandeling. Is er
    /// intussen een nieuwere verversing gestart, dan wordt dit (oudere) antwoord genegeerd en krijgt
    /// de pagina de huidige lijst terug alsof het ophalen slaagde.
    /// </summary>
    public async Task<ApiResult<List<SpeeltijdDto>>> LaadAsync(Func<Task<ApiResult<List<SpeeltijdDto>>>> laad)
    {
        var (resultaat, verouderd) = await VerversAsync(laad);
        return verouderd ? ApiResult<List<SpeeltijdDto>>.Ok(_items) : resultaat;
    }

    public void StartNieuw() => Actief = new SpeeltijdBewerkSessie(new SpeeltijdDto(), isNieuw: true);

    /// <summary>
    /// Opent het formulier voor een bestaande regel. Weigert zolang een eerdere opslag van diezelfde
    /// categorie nog loopt — tot en met de verversing van de lijst erna: de kopie zou anders de waarden
    /// van vóór die opslag bevatten en bij Opslaan het zojuist opgeslagen resultaat weer overschrijven.
    /// De pagina toont die regel intussen als bezig.
    /// </summary>
    public bool StartBewerken(SpeeltijdDto bron)
    {
        if (IsOpslagBezig(bron)) return false;
        Actief = new SpeeltijdBewerkSessie(Kopie(bron), isNieuw: false);
        return true;
    }

    public void Annuleer() => Actief = null;

    public void SluitMelding() => Melding = null;

    public void SluitVerversFout() => VerversFout = null;

    /// <summary>Het bewerkformulier staat direct onder deze regel (#1543).</summary>
    public bool IsInBewerking(SpeeltijdDto regel) =>
        Actief is { IsNieuw: false } sessie && SleutelGelijk(sessie, regel);

    /// <summary>
    /// Een opslag van deze regel is onderweg — ook als het formulier intussen gesloten of vervangen is,
    /// en ook nog tijdens het ophalen van de verse lijst na een geslaagde opslag.
    /// </summary>
    public bool IsOpslagBezig(SpeeltijdDto regel) =>
        _lopend.Any(sessie => !sessie.IsNieuw && SleutelGelijk(sessie, regel));

    /// <summary>
    /// Slaat de actieve sessie op en ververst daarna de lijst. De regel blijft geblokkeerd tot die
    /// verversing is afgerond; vóór de verversing krijgt de regel al lokaal de opgeslagen waarden, zodat
    /// een mislukte verversing nooit oude waarden achterlaat. Geeft <c>true</c> terug als er is opgeslagen.
    /// </summary>
    public async Task<bool> OpslaanAsync(
        Func<SpeeltijdDto, bool, Task<ApiResult<object>>> opslaan,
        Func<Task<ApiResult<List<SpeeltijdDto>>>> laad)
    {
        var sessie = Actief;
        if (sessie is null || sessie.Bezig) return false;

        sessie.Bezig = true;
        sessie.Fout = null;
        _lopend.Add(sessie);
        try
        {
            var resultaat = await opslaan(sessie.Model, sessie.IsNieuw);
            var nogActief = ReferenceEquals(Actief, sessie);
            if (!resultaat.Success)
            {
                var fout = resultaat.ErrorMessage ?? "Opslaan mislukt";
                if (nogActief)
                    sessie.Fout = fout;
                else
                    Melding = $"Opslaan van {sessie.Naam} is mislukt: {fout}";
                return false;
            }

            if (nogActief) Actief = null;
            PasLokaalToe(sessie.Model);
            Gewijzigd?.Invoke();

            var (vers, verouderd) = await VerversAsync(laad);
            if (!verouderd && !vers.Success)
                VerversFout = $"De lijst kon na het opslaan van {sessie.Naam} niet opnieuw worden opgehaald " +
                              $"({vers.ErrorMessage ?? "onbekende fout"}). De regel toont de zojuist opgeslagen waarden.";
            return true;
        }
        finally
        {
            sessie.Bezig = false;
            _lopend.Remove(sessie);
        }
    }

    /// <summary>
    /// Vraagt de lijst op en past het antwoord alleen toe als er intussen geen nieuwere verversing is
    /// gestart. Een ouder antwoord is een oudere momentopname: toepassen zou recentere (lokale of
    /// opgehaalde) waarden overschrijven. Geeft terug of het antwoord verouderd was.
    /// </summary>
    private async Task<(ApiResult<List<SpeeltijdDto>> Resultaat, bool Verouderd)> VerversAsync(
        Func<Task<ApiResult<List<SpeeltijdDto>>>> laad)
    {
        var generatie = ++_laadGeneratie;
        var resultaat = await laad();
        if (generatie != _laadGeneratie)
            return (resultaat, true);
        if (resultaat.Success)
        {
            _items = resultaat.Data ?? new();
            VerversFout = null;
        }
        return (resultaat, false);
    }

    /// <summary>De opgeslagen regel staat direct in de lijst, nog vóór de server hem opnieuw levert.</summary>
    private void PasLokaalToe(SpeeltijdDto opgeslagen)
    {
        var kopie = Kopie(opgeslagen);
        var index = _items.FindIndex(r => string.Equals(r.Leeftijd, opgeslagen.Leeftijd, StringComparison.Ordinal));
        if (index >= 0)
            _items[index] = kopie;
        else
            _items.Add(kopie);
    }

    private static SpeeltijdDto Kopie(SpeeltijdDto bron) => new()
    {
        Leeftijd = bron.Leeftijd,
        Veldafmeting = bron.Veldafmeting,
        WedstrijdTotaal = bron.WedstrijdTotaal,
        WedstrijdHelft = bron.WedstrijdHelft,
        WedstrijdRust = bron.WedstrijdRust,
        StandaardVoorkeurTijd = bron.StandaardVoorkeurTijd
    };

    private static bool SleutelGelijk(SpeeltijdBewerkSessie sessie, SpeeltijdDto regel) =>
        string.Equals(sessie.Model.Leeftijd, regel.Leeftijd, StringComparison.Ordinal);
}
