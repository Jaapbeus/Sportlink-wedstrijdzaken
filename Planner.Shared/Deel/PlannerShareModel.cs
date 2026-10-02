using System;
using System.Collections.Generic;

namespace Planner.Shared.Deel
{
    /// <summary>
    /// Tier- en formaatonafhankelijk documentmodel voor het delen van een planning (epic #1365,
    /// #1363). De PDF-export (<see cref="PlannerPdfGenerator"/>) rendert uitsluitend dit model; de
    /// bestaande HTML-export (<see cref="PlannerHtmlGenerator"/>) blijft daar bewust buiten.
    /// </summary>
    /// <param name="Titel">Kop van het document, bv. "Veldbezetting op zaterdag 3 oktober 2026".</param>
    /// <param name="ClubCode">Club waarvoor het document gemaakt is (de <c>X-Club-Code</c>-scope).</param>
    /// <param name="Peildatum">De speeldag waar de planning over gaat — een kalenderdatum, geen tijdstip.</param>
    /// <param name="Wedstrijden">De regels van de tabel, al in weergavevolgorde.</param>
    public sealed record PlannerShareModel(
        string Titel,
        string ClubCode,
        DateOnly Peildatum,
        IReadOnlyList<PlannerShareWedstrijd> Wedstrijden);

    /// <summary>
    /// Eén regel in de gedeelde planning. Dezelfde kolommen als de bestaande export toont
    /// (Tijd/Team/Tegenstander/Veld/Competitie, plus Scheidsrechter waar bekend) — conform het
    /// "geen extra filtering"-besluit in epic #1365. <c>null</c> betekent: niet bekend.
    /// </summary>
    public sealed record PlannerShareWedstrijd(
        string Tijd,
        string Team,
        string? Tegenstander,
        string? Veld,
        string? Competitie,
        string? Scheidsrechter);

    /// <summary>
    /// De velden die <see cref="PlannerShareModelBuilder.VanVeldbezetting"/> van één regel uit
    /// <c>GET /api/planner/veldbezetting</c> nodig heeft.
    /// <para>
    /// <b>Waarom een interface en geen concreet type.</b> Het veldbezettingsitem bestaat per tier als
    /// eigen DTO (<c>FunctionApp/Planner/PlannerModels.cs</c> en
    /// <c>FunctionApp.Postgres/Planner/AutoPlanService.cs</c>), en <c>Planner.Shared</c> kent geen van
    /// beide. Beide DTO's implementeren deze interface met hun bestaande eigenschappen, zodat de
    /// mapping hier één keer staat in plaats van per tier. Het is een vorm van data, geen
    /// providerabstractie: er zit geen databasetoegang achter (zie
    /// docs/ARCHITECTUUR-DATABASE-TIERS.md §2).
    /// </para>
    /// </summary>
    public interface IVeldbezettingRegel
    {
        string? AanvangsTijd { get; }
        string TeamNaam { get; }
        string Wedstrijd { get; }
        string? Uitteam { get; }
        string? Veld { get; }
        string? Competitiesoort { get; }
    }

    /// <summary>
    /// De velden die <see cref="PlannerShareModelBuilder.VanPlan"/> van één regel uit
    /// <c>POST /api/planner/auto-plan</c> nodig heeft. Zelfde motivatie als
    /// <see cref="IVeldbezettingRegel"/>: beide tiers hebben hun eigen <c>AutoPlanWedstrijdItem</c>.
    /// </summary>
    public interface IPlanWedstrijdRegel
    {
        string TeamNaam { get; }
        string Wedstrijd { get; }
        string? Competitiesoort { get; }
        string? HuidigeTijd { get; }
        string? HuidigeVeld { get; }
        string? OptimaalTijd { get; }
        string? OptimaalVeld { get; }
    }

    /// <summary>Welke kant van de Veld optimalisatie-pagina gedeeld wordt.</summary>
    public enum PlanWeergave
    {
        /// <summary>De stand zoals hij nu in Sportlink staat (tab "Huidig").</summary>
        Huidig,

        /// <summary>Het voorstel van de planner (tab "Optimaal").</summary>
        Optimaal,
    }
}
