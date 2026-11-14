namespace Planner.Shared.Email;

/// <summary>
/// Stand van een bestaande verwerkingsrij voor de idempotentiecontrole (#712).
/// <para>
/// <see cref="AntwoordVerstuurd"/> is de harde grens tegen een dubbel antwoord. Het blijft na AVG-
/// anonimisering beschikbaar; de statuslijst blijft daarnaast een tweede, onafhankelijke grens.
/// </para>
/// </summary>
/// <param name="VerzendPogingOnbeslist">
/// Er is een verzendintentie vastgelegd, maar geen uitkomst. Opnieuw versturen is dan niet veilig,
/// omdat het eerste antwoord al verzonden kan zijn.
/// </param>
public sealed record EmailVerwerkingStand(
    int VerwerkingId,
    string Status,
    int Pogingen,
    bool AntwoordVerstuurd,
    bool VerzendPogingOnbeslist = false);
