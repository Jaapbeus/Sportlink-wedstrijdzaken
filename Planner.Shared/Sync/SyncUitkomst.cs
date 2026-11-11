namespace Planner.Shared.Sync;

/// <summary>
/// Uitkomst van een dataservice-sync (#1779): welke fasen mislukten. Beide tiers gebruiken deze ene
/// vorm, zodat de melding "sync gedeeltelijk mislukt" overal hetzelfde faseoverzicht draagt.
/// </summary>
public sealed class SyncUitkomst
{
    /// <summary>Vaste faselabels; nooit runtimewaarden (de labels komen in publieke diagnostiek).</summary>
    public const string FaseTeams = "teams";
    public const string FaseProgramma = "programma";
    public const string FaseUitslagen = "uitslagen";
    public const string FaseMatchdetails = "matchdetails";
    public const string FaseConfiguratie = "configuratie";

    private readonly List<string> _mislukteFases = new();

    public IReadOnlyList<string> MislukteFases => _mislukteFases;

    public bool GedeeltelijkMislukt => _mislukteFases.Count > 0;

    /// <summary>Registreert de fase als mislukt wanneer <paramref name="mislukt"/> waar is.</summary>
    /// <returns>De doorgegeven waarde, zodat aanroepers <c>partialFailure |= uitkomst.Registreer(...)</c> kunnen blijven gebruiken.</returns>
    public bool Registreer(string fase, bool mislukt)
    {
        if (mislukt && !_mislukteFases.Contains(fase))
            _mislukteFases.Add(fase);
        return mislukt;
    }

    /// <summary>Uitkomst voor een sync die niet kon starten omdat de configuratie ontbreekt.</summary>
    public static SyncUitkomst Configuratie()
    {
        var u = new SyncUitkomst();
        u.Registreer(FaseConfiguratie, true);
        return u;
    }

    /// <summary>Foutmelding voor syncjobs; alleen vaste faselabels (geen runtimewaarden).</summary>
    public string JobFoutmelding()
        => "Sync gedeeltelijk mislukt (fases: " + string.Join(", ", _mislukteFases)
           + ") — zie het functielog voor details.";

    /// <summary>Getypeerde uitzondering voor de timer-/HTTP-paden (#1081, #1779).</summary>
    public SyncGedeeltelijkMisluktException NaarUitzondering() => new(_mislukteFases);

    /// <summary>
    /// Timerpad van beide tiers (#1081, #1779): een run met mislukte fases mag niet als Success
    /// eindigen. Alle werk is dan al gedaan; de uitzondering markeert alleen de invocatie.
    /// </summary>
    public void GooiAlsMislukt()
    {
        if (GedeeltelijkMislukt)
            throw NaarUitzondering();
    }
}

/// <summary>
/// De dagelijkse sync is bewust gedeeltelijk mislukt (#1081). Een eigen type, zodat de
/// foutrapportage een eigen categorie en het faseoverzicht kan tonen in plaats van "Configuratie/status"
/// (#1779). Erft van <see cref="InvalidOperationException"/> voor compatibiliteit met bestaande catches.
/// </summary>
public sealed class SyncGedeeltelijkMisluktException : InvalidOperationException
{
    public IReadOnlyList<string> Fases { get; }

    /// <summary>
    /// Bewust een vast bericht zonder faselijst: de fingerprint van de foutrapportage hasht type +
    /// bericht, en één fingerprint per faseset zou per combinatie een eigen publiek issue openen
    /// (#1779). De fases staan in <see cref="Fases"/> en in het diagnostiekblok.
    /// </summary>
    public const string Bericht =
        "Synchronisatie gedeeltelijk mislukt — zie het faseoverzicht en de voorgaande logregels. "
        + "De laatste-sync-tijd is bewust niet bijgewerkt.";

    public SyncGedeeltelijkMisluktException(IReadOnlyList<string> fases)
        : base(Bericht)
    {
        Fases = fases.ToArray();
    }
}
