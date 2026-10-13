namespace Planner.Shared.Sync;

/// <summary>Eén seizoen van een club: naam ("2026-2027"), eerste en laatste dag.</summary>
public sealed record SeizoenRij(string Naam, DateOnly Van, DateOnly Tot);

/// <summary>
/// De seizoenskalender van één club, afgeleid uit zijn eigen startmaand (#1715,
/// <see cref="SeizoenStartMaand"/>). Tier-onafhankelijke regel: het seizoen
/// <c>jaar-(jaar+1)</c> loopt van de eerste dag van de startmaand in <c>jaar</c> tot de dag vóór de start
/// van het volgende seizoen (het seizoenseinde). Gebruikt door de Postgres-seizoensprocedure en door het
/// herbouwen van de seizoensrijen na een wijziging van de startmaand op beide tiers; de SQL Server-procedure
/// <c>sp_UpdateClubSeasonTable</c> rekent hetzelfde in T-SQL (<c>DATEADD</c>, geen ongeldige maand bij januari).
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
    /// Vanaf welke datum bestaat het seizoen dat in <paramref name="jaar"/> begint: twee maanden vóór de
    /// start. Intervalrekenkunde, dus ook geldig bij januari/februari (maand − 2 ≤ 0); de datum ligt dan
    /// in het kalenderjaar ervóór (1 november resp. 1 december).
    /// </summary>
    public static DateOnly DoorrolDatum(int jaar, int startMaand)
        => new DateOnly(jaar, 1, 1).AddMonths(SeizoenStartMaand.OfStandaard(startMaand) - 3);

    /// <summary>
    /// De startjaren waarvan het seizoen op <paramref name="vandaag"/> moet bestaan omdat de doorroldatum
    /// bereikt is: het seizoen dat dit kalenderjaar begint, én het seizoen dat volgend kalenderjaar begint.
    /// Dat laatste is nodig voor een start in januari of februari: de doorroldatum ligt dan op 1 november
    /// resp. 1 december van dit jaar (Codex-review #1743 ronde 1). Bij maart tot en met december ligt
    /// die van volgend jaar altijd in volgend jaar, dus blijft het gedrag (o.a. juli) gelijk.
    /// </summary>
    public static IReadOnlyList<int> DoorTeRollenStartjaren(int startMaand, DateOnly vandaag)
        => new[] { vandaag.Year, vandaag.Year + 1 }.Where(j => vandaag >= DoorrolDatum(j, startMaand)).ToList();

    /// <summary>
    /// De seizoenen die een club minimaal heeft (zoals een verse installatie ze krijgt): de laatste twee
    /// seizoenen die vóór dit kalenderjaar begonnen, plus elk seizoen waarvan <see cref="DoorrolDatum"/>
    /// bereikt is (<see cref="DoorTeRollenStartjaren"/>).
    /// </summary>
    public static IReadOnlyList<SeizoenRij> Basis(int startMaand, DateOnly vandaag)
    {
        var rijen = new List<SeizoenRij>
        {
            Seizoen(vandaag.Year - 2, startMaand),
            Seizoen(vandaag.Year - 1, startMaand),
        };
        rijen.AddRange(DoorTeRollenStartjaren(startMaand, vandaag).Select(j => Seizoen(j, startMaand)));
        return rijen;
    }
}
