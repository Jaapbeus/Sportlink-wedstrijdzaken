using Planner.Shared.Deel;

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
// #1856: AutoPlanRequest, AutoPlanResponse en AutoPlanToepassenResponse staan in de gedeelde bron
// GedeeldeBron/PlannerDto/AutoPlanModels.cs. Deze twee typen blijven tijdelijk per tier, omdat open
// PR's (#1823, #1828, #1843) ze wijzigen; daarna volgen ze naar de gedeelde bron (zie
// docs/ARCHITECTUUR-CODEKWALITEIT.md, regel 2b).
// IPlanWedstrijdRegel (#1363): de gedeelde PDF-export leest deze regel zonder eigen mapping per tier.
public class AutoPlanWedstrijdItem : IPlanWedstrijdRegel
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

    /// <summary>
    /// Actieve teamregel <c>BufferVoor</c> in minuten, of null zonder regel (#1430). Ruwe waarde, niet
    /// de effectieve: de client neemt het maximum met de algemene buffer (PlanningBufferRegels).
    /// </summary>
    public int? TeamBufferVoor { get; set; }

    /// <summary>Actieve teamregel <c>BufferNa</c> in minuten, of null zonder regel (#1430).</summary>
    public int? TeamBufferNa { get; set; }
}

public class AutoPlanToepassenRequest
{
    public string Datum { get; set; } = string.Empty;
    public int? BufferMinuten { get; set; }
}
