namespace Planner.Shared.Sync;

/// <summary>Eén seizoen van een club: naam ("2026-2027"), eerste en laatste dag.</summary>
public sealed record SeizoenRij(string Naam, DateOnly Van, DateOnly Tot);

/// <summary>
/// De seizoenskalender van één club, afgeleid uit zijn eigen startmaand (#1715,
/// <see cref="SeizoenStartMaand"/>). Tier-onafhankelijke regel: het seizoen
/// <c>jaar-(jaar+1)</c> loopt van de eerste dag van de startmaand in <c>jaar</c> tot de dag vóór de start
/// van het volgende seizoen (het seizoenseinde). Gebruikt door de Postgres-seizoensprocedure en door het
/// herbouwen van de seizoensrijen na een wijziging van de startmaand op beide tiers; de SQL Server-procedure
/// <c>sp_UpdateSeasonTable</c> rekent hetzelfde in T-SQL (<c>DATEADD</c>, geen ongeldige maand bij januari).
/// </summary>
public static class SeizoenKalender
{
    /// <summary>Het seizoen dat in <paramref name="startjaar"/> begint.</summary>
    public static SeizoenRij Seizoen(int startjaar, int startMaand)
    {
        var maand = SeizoenStartMaand.OfStandaard(startMaand);
        var van = new DateOnly(startjaar, maand, 1);
        return new SeizoenRij($"{startjaar}-{startjaar + 1}", van, new DateOnly(startjaar + 1, maand, 1).AddDays(-1));
    }

    /// <summary>
    /// Vanaf welke datum bestaat het seizoen dat in het jaar van <paramref name="vandaag"/> begint: twee
    /// maanden vóór de start. Intervalrekenkunde, dus ook geldig bij januari/februari (maand − 2 ≤ 0).
    /// </summary>
    public static DateOnly DoorrolDatum(int jaar, int startMaand)
        => new DateOnly(jaar, 1, 1).AddMonths(SeizoenStartMaand.OfStandaard(startMaand) - 3);

    /// <summary>
    /// De seizoenen die een club minimaal heeft (zoals een verse installatie ze krijgt): de laatste twee
    /// afgeronde seizoenen, plus het seizoen dat dit jaar begint zodra <see cref="DoorrolDatum"/> bereikt is.
    /// </summary>
    public static IReadOnlyList<SeizoenRij> Basis(int startMaand, DateOnly vandaag)
    {
        var rijen = new List<SeizoenRij>
        {
            Seizoen(vandaag.Year - 2, startMaand),
            Seizoen(vandaag.Year - 1, startMaand),
        };
        if (vandaag >= DoorrolDatum(vandaag.Year, startMaand))
            rijen.Add(Seizoen(vandaag.Year, startMaand));
        return rijen;
    }
}
