namespace SportlinkFunction.Email;

/// <summary>
/// Eén e-mailbijlage, kanaal-agnostisch (#561). Klein en immutable — bevat alleen wat nodig is om
/// een Graph FileAttachment te bouwen.
/// </summary>
public sealed record EmailBijlage(string Bestandsnaam, byte[] Inhoud, string ContentType);
