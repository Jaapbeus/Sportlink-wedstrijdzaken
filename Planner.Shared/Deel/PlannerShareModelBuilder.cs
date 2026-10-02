using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Planner.Shared.Deel
{
    /// <summary>
    /// Bouwt een <see cref="PlannerShareModel"/> uit de gegevens die de Planning- en de Veld
    /// optimalisatie-pagina vandaag al tonen (#1363). Pure mapping: geen DI, geen I/O, geen
    /// klok — de datum komt altijd van de aanroeper.
    /// </summary>
    public static class PlannerShareModelBuilder
    {
        /// <summary>Plaatshouder in de tijdkolom voor een wedstrijd zonder aanvangstijd.</summary>
        public const string GeenTijd = "—";

        private const string TegenstanderScheiding = " - ";

        private static readonly CultureInfo Nl = CultureInfo.GetCultureInfo("nl-NL");

        /// <summary>
        /// Voor de Planning-pagina: wat er nu in Sportlink gepland staat (<c>GET /api/planner/veldbezetting</c>).
        /// De tegenstander is <see cref="IVeldbezettingRegel.Uitteam"/> — dezelfde kolom die
        /// <c>BlazorAdmin/Pages/Planning.razor</c> als "Tegenstander" toont.
        /// </summary>
        public static PlannerShareModel VanVeldbezetting(
            IEnumerable<IVeldbezettingRegel> items, DateOnly datum, string clubCode)
        {
            ArgumentNullException.ThrowIfNull(items);

            var regels = items
                .Select(i => new PlannerShareWedstrijd(
                    Tijd: TijdOfPlaatshouder(i.AanvangsTijd),
                    Team: i.TeamNaam,
                    Tegenstander: LeegAlsNull(i.Uitteam),
                    Veld: LeegAlsNull(i.Veld),
                    Competitie: LeegAlsNull(i.Competitiesoort),
                    Scheidsrechter: null))
                .ToList();

            return new PlannerShareModel(
                Titel: $"Veldbezetting op {DatumTekst(datum)}",
                ClubCode: clubCode,
                Peildatum: datum,
                Wedstrijden: Sorteer(regels));
        }

        /// <summary>
        /// Voor de Veld optimalisatie-pagina (<c>POST /api/planner/auto-plan</c>): de tab "Huidig"
        /// of "Optimaal". De titel zegt welke van de twee, zodat een gedeeld document nooit voor
        /// de andere kan worden aangezien.
        /// <para>
        /// Het auto-plan-item heeft geen apart uitteam-veld; de tegenstander wordt afgeleid uit
        /// <see cref="IPlanWedstrijdRegel.Wedstrijd"/> ("Thuis - Uit") via
        /// <see cref="TegenstanderUitWedstrijd"/>. Lukt dat niet eenduidig, dan blijft hij leeg —
        /// nooit gegokt.
        /// </para>
        /// </summary>
        public static PlannerShareModel VanPlan(
            IEnumerable<IPlanWedstrijdRegel> wedstrijden, DateOnly datum, string clubCode, PlanWeergave weergave)
        {
            ArgumentNullException.ThrowIfNull(wedstrijden);

            var optimaal = weergave == PlanWeergave.Optimaal;
            var regels = wedstrijden
                .Select(w => new PlannerShareWedstrijd(
                    Tijd: TijdOfPlaatshouder(optimaal ? w.OptimaalTijd : w.HuidigeTijd),
                    Team: w.TeamNaam,
                    Tegenstander: TegenstanderUitWedstrijd(w.Wedstrijd),
                    Veld: LeegAlsNull(optimaal ? w.OptimaalVeld : w.HuidigeVeld),
                    Competitie: LeegAlsNull(w.Competitiesoort),
                    Scheidsrechter: null))
                .ToList();

            var soort = optimaal ? "Optimale planning" : "Huidige planning";
            return new PlannerShareModel(
                Titel: $"{soort} op {DatumTekst(datum)}",
                ClubCode: clubCode,
                Peildatum: datum,
                Wedstrijden: Sorteer(regels));
        }

        /// <summary>
        /// Het tweede deel van een wedstrijdomschrijving "Thuis - Uit", of <c>null</c> als de tekst
        /// niet precies één <c>" - "</c>-scheiding bevat. Een teamnaam als <c>JO10-1</c> heeft geen
        /// spaties rond het streepje en splitst dus niet.
        /// </summary>
        public static string? TegenstanderUitWedstrijd(string? wedstrijd)
        {
            if (string.IsNullOrWhiteSpace(wedstrijd))
                return null;

            var delen = wedstrijd.Split(TegenstanderScheiding);
            return delen.Length == 2 ? LeegAlsNull(delen[1]) : null;
        }

        /// <summary>Lange Nederlandse datum, bv. "zaterdag 3 oktober 2026".</summary>
        public static string DatumTekst(DateOnly datum) => datum.ToString("dddd d MMMM yyyy", Nl);

        // Op tijd, wedstrijden zonder tijd achteraan — zelfde volgorde als de veldbezettingsquery
        // ("99:99"-terugval), daarna op team zodat de volgorde deterministisch is.
        private static IReadOnlyList<PlannerShareWedstrijd> Sorteer(List<PlannerShareWedstrijd> regels) =>
            regels
                .OrderBy(r => r.Tijd == GeenTijd ? "99:99" : r.Tijd, StringComparer.Ordinal)
                .ThenBy(r => r.Team, StringComparer.Ordinal)
                .ToList();

        private static string TijdOfPlaatshouder(string? tijd) =>
            string.IsNullOrWhiteSpace(tijd) ? GeenTijd : tijd.Trim();

        private static string? LeegAlsNull(string? waarde) =>
            string.IsNullOrWhiteSpace(waarde) ? null : waarde.Trim();
    }
}
