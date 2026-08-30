namespace Planner.Shared;

/// <summary>
/// Splitst een Sportlink-veldstring in het veldnummer uit <c>dbo.Velden</c>/<c>public.velden</c>
/// en de subpositie die Sportlink erachter zet — tier-agnostisch (#819).
/// <para>
/// Geëxtraheerd uit <c>FunctionApp/Planner/Services/PlannerShared.ResolveVeld</c> (#707/#719),
/// woordelijk gelijk gedrag. Zie <see cref="VeldNormalisatie"/> voor de motivatie waarom deze
/// pure matching-logica gedeeld wordt tussen tiers in plaats van per tier herbouwd: het
/// alternatief (een eigen Postgres-side SQL-vertaling van deze matching in de nieuwe view) zou
/// een derde, onafhankelijke kopie zijn naast de bestaande twee (C# hier, en de SQL Server-view
/// <c>planner.AlleWedstrijdenOpVeld</c>) — precies het onderhoudsrisico dat #719 al blootlegde.
/// De Postgres-tier gebruikt daarom deze zelfde implementatie in plaats van een eigen
/// <c>OUTER APPLY</c>-vertaling; zie de planner-view-generator in <c>Database.Postgres</c>.
/// </para>
/// <para>
/// Een treffer is een exact gelijke veldnaam, óf een veldnaam gevolgd door een spatie en de
/// subpositie — nooit een langer veldnummer, zodat "veld 10" niet op "veld 1" valt. Langste
/// veldnaam eerst: bestaat naast "veld 1" ook "veld 1 achter", dan hoort "veld 1 achter B" bij
/// dat tweede veld en is "achter" geen subpositie van veld 1.
/// </para>
/// </summary>
public static class VeldResolver
{
    /// <returns>
    /// Veldnummer, of <c>0</c> als geen enkel veld matcht, plus de subpositie in hoofdletters of
    /// <c>null</c> als die ontbreekt.
    /// </returns>
    public static (int VeldNummer, string? Subpositie) Resolve(
        string? sportlinkVeld, IEnumerable<(string? VeldNaam, int VeldNummer)> velden)
    {
        var gezocht = VeldNormalisatie.Normaliseer(sportlinkVeld);
        if (gezocht.Length == 0) return (0, null);

        foreach (var veld in velden
                     .Select(v => (Naam: VeldNormalisatie.Normaliseer(v.VeldNaam), v.VeldNummer))
                     .Where(v => v.Naam.Length > 0)
                     .OrderByDescending(v => v.Naam.Length))
        {
            if (gezocht == veld.Naam) return (veld.VeldNummer, null);
            if (!gezocht.StartsWith(veld.Naam + " ", StringComparison.Ordinal)) continue;

            var subpositie = gezocht[(veld.Naam.Length + 1)..].Trim();
            return (veld.VeldNummer, subpositie.Length == 0 ? null : subpositie.ToUpperInvariant());
        }
        return (0, null);
    }

    /// <inheritdoc cref="Resolve(string?, IEnumerable{ValueTuple{string?, int}})"/>
    public static (int VeldNummer, string? Subpositie) Resolve(
        string? sportlinkVeld, IReadOnlyDictionary<string, int> veldenPerNaam)
        => Resolve(sportlinkVeld, veldenPerNaam.Select(kv => ((string?)kv.Key, kv.Value)));
}
