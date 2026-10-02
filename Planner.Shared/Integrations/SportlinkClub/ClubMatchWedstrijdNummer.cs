namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Het wedstrijdnummer van een zelf aangemaakte oefenwedstrijd (#1437): <c>YYMMDD</c> + een
/// tweecijferig volgnummer per speeldag, bijv. 26100201 en 26100202 voor 2 oktober 2026. Het
/// volgnummer komt uit een per club en datum bijgehouden teller (tier-eigen, atomair); alleen de
/// opmaak staat hier, zodat beide tiers dezelfde nummers maken.
/// </summary>
public static class ClubMatchWedstrijdNummer
{
    /// <summary>Hoogste volgnummer per dag: het nummer heeft twee cijfers voor het volgnummer.</summary>
    public const int MaxVolgnummer = 99;

    public static string TeVeelOpEenDagMelding(DateOnly datum) =>
        $"Voor {datum:dd-MM-yyyy} zijn al {MaxVolgnummer} wedstrijdnummers uitgegeven — er kunnen op één dag niet meer wedstrijden worden aangemaakt.";

    /// <summary>Zet datum en volgnummer (1..99) om naar het wedstrijdnummer; buiten bereik geeft een <see cref="ArgumentOutOfRangeException"/>.</summary>
    public static long Formatteer(DateOnly datum, int volgnummer)
    {
        if (volgnummer is < 1 or > MaxVolgnummer)
            throw new ArgumentOutOfRangeException(nameof(volgnummer), volgnummer, $"Volgnummer moet tussen 1 en {MaxVolgnummer} liggen.");
        var yymmdd = (datum.Year % 100) * 10000L + datum.Month * 100L + datum.Day;
        return yymmdd * 100 + volgnummer;
    }
}
