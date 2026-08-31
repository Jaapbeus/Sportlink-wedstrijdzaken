namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Wire-contract-DTO's voor <c>POST /api/planner/auto-plan</c> en
/// <c>/auto-plan/toepassen</c> — Postgres-tier-tegenhanger van de gelijknamige typen in
/// <c>FunctionApp/Planner/PlannerModels.cs</c> (issue 888 vervolg, §42).
/// <para>
/// Bewuste duplicatie, geen verwijzing naar de andere boom — zelfde motivatie als
/// <see cref="MatchModels"/> en <see cref="AvailabilityModels"/>: twee volledig gescheiden
/// implementatiebomen (ARCHITECTUUR-DATABASE-TIERS.md §2), en pure DTO's zonder gedrag hebben geen
/// "one location"-eis. Veldnamen letterlijk gelijk zodat de JSON op beide tiers identiek is.
/// </para>
/// <para>
/// <c>OptimalisatieSuggestie</c> staat hier bewust NIET bij: dat type is invoer voor de gedeelde
/// <see cref="Planner.Shared.PlannerHtmlGenerator"/> en is bij §42 juist naar
/// <c>Planner.Shared</c> verhuisd.
/// </para>
/// </summary>
public class AutoPlanRequest
{
    public string Datum { get; set; } = string.Empty;
    public int? BufferMinuten { get; set; }
}

public class AutoPlanWedstrijdItem
{
    public long? WedstrijdCode { get; set; }
    public string Wedstrijd { get; set; } = string.Empty;
    public string TeamNaam { get; set; } = string.Empty;
    public string? LeeftijdsCategorie { get; set; }
    public string? Competitiesoort { get; set; }
    public int DuurMinuten { get; set; }
    public decimal Veldafmeting { get; set; }

    // Huidige situatie
    public string? HuidigeVeld { get; set; }
    public string? HuidigeTijd { get; set; }
    public bool HeeftVeld { get; set; }
    public bool HeeftTijd { get; set; }

    // Optimale situatie
    public int? OptimaalVeldNummer { get; set; }
    public string? OptimaalVeldNaam { get; set; }
    public string? OptimaalVeld { get; set; }  // Sportlink-formaat "veld 3 A"
    public string? OptimaalTijd { get; set; }  // "09:00"

    /// <summary>
    /// "nieuw-slot" | "wijziging" | "ongewijzigd" | "niet-inplanbaar" | "onbekend-team".
    /// Zegt alleen of de planner de wedstrijd verplaatst t.o.v. de HUIDIGE stand; of hij op de
    /// gewenste voorkeurstijd staat, staat in <see cref="VoorkeurStatus"/> (#666).
    /// </summary>
    public string Status { get; set; } = "ongewijzigd";
    public string? NietInplanbaaarReden { get; set; }

    // Voorkeurstijd-informatie (null = geen voorkeur en geen default geconfigureerd)
    public string? VoorkeurTijd { get; set; }
    public int? VoorkeurAfwijkingMinuten { get; set; }

    /// <summary>
    /// Waar de voorkeurstijd uit komt (#666): "regel" (public.teamregels VoorkeurVeld met tijd),
    /// "team" (public.teamvoorkeurtijden) of "leeftijd" (public.speeltijden.standaardvoorkeurtijd).
    /// </summary>
    public string? VoorkeurBron { get; set; }

    /// <summary>
    /// "op-tijd" (exact), "kleine-afwijking" (t/m 15 min), "grote-afwijking", "geen-voorkeur".
    /// </summary>
    public string VoorkeurStatus { get; set; } = "geen-voorkeur";

    /// <summary>Voorkeursveld uit een 'VoorkeurVeld'-teamregel; null als die regel er niet is.</summary>
    public int? VoorkeurVeldNummer { get; set; }

    /// <summary>False als er een voorkeursveld was maar de planner een ander veld moest kiezen.</summary>
    public bool? VoorkeurVeldToegepast { get; set; }
}

public class AutoPlanResponse
{
    public string Datum { get; set; } = string.Empty;
    public int TotaalWedstrijden { get; set; }
    public int ZonderVeld { get; set; }
    public int ZonderTijd { get; set; }
    public int TeWijzigen { get; set; }
    public int NietInplanbaar { get; set; }
    public string? GeschatteEindTijd { get; set; }
    public List<AutoPlanWedstrijdItem> Wedstrijden { get; set; } = new();
    public string HuidigeHtml { get; set; } = string.Empty;
    public string OptimaleHtml { get; set; } = string.Empty;
}

public class AutoPlanToepassenRequest
{
    public string Datum { get; set; } = string.Empty;
    public int? BufferMinuten { get; set; }
}

public class AutoPlanToepassenResponse
{
    public int Bijgewerkt { get; set; }
    public int Mislukt { get; set; }
    public List<string> Fouten { get; set; } = new();
}
