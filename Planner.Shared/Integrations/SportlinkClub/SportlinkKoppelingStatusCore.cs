using System.Globalization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Niveau van de Sportlink-koppeling voor een functionele rol, zoals de werkpagina's het tonen (#1773).</summary>
public enum SportlinkKoppelingNiveau
{
    /// <summary>Geen reden om een melding te tonen.</summary>
    Ok,
    /// <summary>Automatisch inloggen faalde tijdelijk; de koppeling kan nog werken, maar herstel loopt.</summary>
    Waarschuwing,
    /// <summary>Acties richting Sportlink gaan falen: niet gekoppeld, geblokkeerd of sessie verlopen.</summary>
    Verbroken,
}

/// <summary>Niet-geheime samenvatting van de koppelingsstatus; bevat nooit een tokenwaarde of credential.</summary>
public sealed record SportlinkKoppelingStatus(
    string RolNaam,
    SportlinkKoppelingNiveau Niveau,
    string? Melding,
    DateTimeOffset? LaatsteLoginUtc)
{
    public bool IsVerbroken => Niveau == SportlinkKoppelingNiveau.Verbroken;
}

/// <summary>
/// Pure statusbepaling (#1773): vertaalt de opgeslagen auto-loginstate en de aanwezigheid van een
/// refresh-token naar één niveau + melding, zodat "Wedstrijd aanmaken", "Wijzigingsverzoeken" en de
/// Sportlink-kolom de gebruiker vóóraf waarschuwen in plaats van pas na een mislukte actie. Dezelfde
/// drempels als <see cref="SportlinkAutoLoginCoordinator"/> (3 mislukkingen, retrydatum jaar 9999 =
/// permanente blokkade) en de absolute sessieduur van tien uur (SPORTLINK-AUTOLOGIN.md).
/// </summary>
public static class SportlinkKoppelingStatusCore
{
    /// <summary>Sportlinks absolute sessieduur: daarna faalt tokenverversing en is een nieuwe login nodig.</summary>
    public static readonly TimeSpan AbsoluteSessieDuur = TimeSpan.FromHours(10);

    private const int MaxMislukkingen = 3;

    public const string NietGekoppeldTekst =
        "Geen Sportlink-koppeling voor rol '{0}' — acties richting Sportlink werken niet. Registreer eerst een koppeling via Beheer > Sportlink Web Extension.";
    public const string GeblokkeerdTekst =
        "De Sportlink-koppeling voor rol '{0}' is verbroken: automatisch inloggen is geblokkeerd. Acties richting Sportlink werken niet totdat de inloggegevens opnieuw zijn opgeslagen via Beheer > Sportlink Web Extension.";
    public const string SessieVerlopenTekst =
        "De Sportlink-koppeling voor rol '{0}' is verlopen en kon niet automatisch worden hersteld. Acties richting Sportlink werken niet; controleer de inloggegevens via Beheer > Sportlink Web Extension.";
    public const string TijdelijkMisluktTekst =
        "Automatisch inloggen bij Sportlink is tijdelijk mislukt; een nieuwe poging volgt vanzelf. Acties richting Sportlink kunnen nu falen.";

    /// <param name="rolNaam">Functionele rol (altijd <see cref="SportlinkEndpointCore.RolWedstrijdzaken"/>).</param>
    /// <param name="state">Opgeslagen auto-loginstate; <c>null</c> als er geen rij is.</param>
    /// <param name="refreshTokenAanwezig">Of er een duurzaam refresh-token staat (de waarde zelf blijft buiten beeld).</param>
    /// <param name="nuUtc">Huidige tijd, voor de sessieduur-beoordeling.</param>
    public static SportlinkKoppelingStatus Bepaal(string rolNaam, SportlinkAutoLoginState? state, bool refreshTokenAanwezig, DateTimeOffset nuUtc)
    {
        var credentials = state?.Credentials is not null;
        if (!credentials && !refreshTokenAanwezig)
            return Maak(rolNaam, SportlinkKoppelingNiveau.Verbroken, NietGekoppeldTekst, state);

        var permanentGeblokkeerd = state is not null &&
            (state.FailureCount >= MaxMislukkingen || state.RetryAfterUtc?.Year == 9999);
        if (permanentGeblokkeerd)
            return Maak(rolNaam, SportlinkKoppelingNiveau.Verbroken, GeblokkeerdTekst, state);

        var tijdelijkMislukt = state is not null && state.FailureCount > 0 && state.RetryAfterUtc > nuUtc;
        if (tijdelijkMislukt)
        {
            // Zonder geldige sessie én zonder refresh-token is er niets om op terug te vallen.
            var sessieVerlopen = state!.LastLoginUtc is null || nuUtc - state.LastLoginUtc.Value > AbsoluteSessieDuur;
            return sessieVerlopen && !refreshTokenAanwezig
                ? Maak(rolNaam, SportlinkKoppelingNiveau.Verbroken, SessieVerlopenTekst, state)
                : Maak(rolNaam, SportlinkKoppelingNiveau.Waarschuwing, TijdelijkMisluktTekst, state);
        }

        return Maak(rolNaam, SportlinkKoppelingNiveau.Ok, null, state);
    }

    private static SportlinkKoppelingStatus Maak(string rolNaam, SportlinkKoppelingNiveau niveau, string? sjabloon, SportlinkAutoLoginState? state) =>
        new(rolNaam, niveau, sjabloon is null ? null : string.Format(CultureInfo.InvariantCulture, sjabloon, rolNaam), state?.LastLoginUtc);
}
