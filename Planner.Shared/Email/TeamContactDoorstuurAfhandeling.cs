namespace Planner.Shared.Email;

/// <summary>
/// Persisted state for one automatic team-contact forwarding action. A missing database value is
/// the legacy-unknown state; newly inserted rows must explicitly start at <see cref="NietGestart"/>.
/// </summary>
public enum TeamContactDoorstuurStatus
{
    NietGestart,
    Bezig,
    Geaccepteerd,
    NietVerstuurd,
    Onbekend
}

/// <summary>Terminal forwarding result consumed by the shared reply flow.</summary>
public enum TeamContactDoorstuurUitkomst
{
    Doorgestuurd,
    NietDoorgestuurd,
    Onbekend
}

/// <summary>Result of an atomic claim of the one permitted forwarding attempt.</summary>
public enum TeamContactDoorstuurClaim
{
    Geclaimd,
    AlGeaccepteerd,
    NietBeschikbaar
}

/// <summary>Outcome from the tier adapter, which resolves the recipient and calls its Graph adapter.</summary>
public enum TeamContactDoorstuurVerzendresultaat
{
    Geaccepteerd,
    GeenOntvanger,
    NietBevestigd
}

/// <summary>The tier-neutral data required to forward a team-contact request.</summary>
public sealed record TeamContactDoorstuurVerzoek(
    string? TeamNaam,
    string Afzender,
    string AfzenderNaam,
    string Inhoud);

/// <summary>
/// Durable storage boundary. <see cref="ClaimAsync"/> must atomically change only an explicit
/// <see cref="TeamContactDoorstuurStatus.NietGestart"/> row to <see cref="TeamContactDoorstuurStatus.Bezig"/>.
/// Null/legacy, in-progress, accepted, and terminal rows must never be claimable.
/// </summary>
public interface ITeamContactDoorstuurPersistentie
{
    Task<TeamContactDoorstuurClaim> ClaimAsync(int verwerkingId);

    /// <summary>Returns true only when exactly the claimed row was durably moved to the terminal state.</summary>
    Task<bool> MarkeerTerminalAsync(int verwerkingId, TeamContactDoorstuurStatus status);
}

/// <summary>
/// Coordinates exactly one durable forwarding attempt. Claiming happens before the adapter can
/// perform recipient lookup or Graph I/O. Any unconfirmed result stays non-retryable and requires
/// manual review; a confirmed Graph acceptance is usable only after its durable write succeeds.
/// </summary>
public static class TeamContactDoorstuurAfhandeling
{
    public static async Task<TeamContactDoorstuurUitkomst> VoerEenmaligUitAsync(
        int verwerkingId,
        ITeamContactDoorstuurPersistentie persistentie,
        Func<Task<TeamContactDoorstuurVerzendresultaat>> verstuurAsync)
    {
        TeamContactDoorstuurClaim claim;
        try
        {
            claim = await persistentie.ClaimAsync(verwerkingId);
        }
        catch
        {
            return TeamContactDoorstuurUitkomst.Onbekend;
        }

        if (claim == TeamContactDoorstuurClaim.AlGeaccepteerd)
            return TeamContactDoorstuurUitkomst.Doorgestuurd;
        if (claim != TeamContactDoorstuurClaim.Geclaimd)
            return TeamContactDoorstuurUitkomst.Onbekend;

        TeamContactDoorstuurVerzendresultaat resultaat;
        try
        {
            resultaat = await verstuurAsync();
        }
        catch
        {
            await ProbeerOnbekendVastTeLeggenAsync(verwerkingId, persistentie);
            return TeamContactDoorstuurUitkomst.Onbekend;
        }

        var status = resultaat switch
        {
            TeamContactDoorstuurVerzendresultaat.Geaccepteerd => TeamContactDoorstuurStatus.Geaccepteerd,
            TeamContactDoorstuurVerzendresultaat.GeenOntvanger => TeamContactDoorstuurStatus.NietVerstuurd,
            _ => TeamContactDoorstuurStatus.Onbekend
        };

        try
        {
            if (!await persistentie.MarkeerTerminalAsync(verwerkingId, status))
                return TeamContactDoorstuurUitkomst.Onbekend;
        }
        catch
        {
            // In particular, a failed Accepted write leaves the row non-claimable (Bezig).
            return TeamContactDoorstuurUitkomst.Onbekend;
        }

        return status switch
        {
            TeamContactDoorstuurStatus.Geaccepteerd => TeamContactDoorstuurUitkomst.Doorgestuurd,
            TeamContactDoorstuurStatus.NietVerstuurd => TeamContactDoorstuurUitkomst.NietDoorgestuurd,
            _ => TeamContactDoorstuurUitkomst.Onbekend
        };
    }

    private static async Task ProbeerOnbekendVastTeLeggenAsync(
        int verwerkingId, ITeamContactDoorstuurPersistentie persistentie)
    {
        try
        {
            await persistentie.MarkeerTerminalAsync(verwerkingId, TeamContactDoorstuurStatus.Onbekend);
        }
        catch
        {
            // The atomic claim itself remains the no-retry boundary if this write also fails.
        }
    }

    public static (string Onderwerp, string Inhoud) BouwReviewVoorstel()
        => ("Uw vraag over teambegeleiding",
            "We hebben uw vraag over teambegeleiding ontvangen. "
            + "De doorstuurstatus is niet bevestigd; de coördinator beoordeelt uw verzoek.");
}
