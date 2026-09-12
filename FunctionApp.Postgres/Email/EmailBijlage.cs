namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/EmailBijlage.cs</c> (#972 — port van
/// EmailProcessorFunction). Eén e-mailbijlage, kanaal-agnostisch. Klein en immutable — bevat
/// alleen wat nodig is om een Graph FileAttachment te bouwen.
/// <para>
/// Op deze tier altijd ongebruikt in de praktijk: de enige aanroeper die een bijlage vult
/// (#561, KNVB-kalender-PDF bij "verzet zonder datum") is niet vertaald — zie de klassekop van
/// <c>FunctionApp.Postgres.Processing.BerichtPipeline</c>. Het type blijft in de
/// <c>SendReplyAsync</c>-signatuur voor gelijkvormigheid met <c>IEmailGraphService</c>.
/// </para>
/// </summary>
public sealed record EmailBijlage(string Bestandsnaam, byte[] Inhoud, string ContentType);
