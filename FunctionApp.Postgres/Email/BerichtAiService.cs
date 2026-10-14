using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Planner.Shared.Email;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-omhulsel om <see cref="BerichtAiKern"/> (#1759, slice 7 van epic #1716): de
/// prompt- en parselogica staat in <c>Planner.Shared</c>. Dit omhulsel levert alleen de clubnaam
/// uit <see cref="PostgresAppSettings"/> en de lokale klok (de datumregel in de system prompt is
/// NL-lokaal; zie docs/ARCHITECTUUR-AI-SERVICES.md).
/// </summary>
public class BerichtAiService
{
    private readonly BerichtAiKern _kern;

    public BerichtAiService(ILogger<BerichtAiService> logger, IChatClient chatClient)
        => _kern = new BerichtAiKern(logger, chatClient, HaalClubNaam, () => DateTime.Now);

    private static string HaalClubNaam()
    {
        var clubNaam = PostgresAppSettings.GetSetting("clubName");
        if (string.IsNullOrWhiteSpace(clubNaam))
            throw new InvalidOperationException("Vereiste instelling 'clubName' ontbreekt of is leeg in public.appsettings");
        return clubNaam;
    }

    /// <inheritdoc cref="BerichtAiKern.ClassificeerBerichtAsync"/>
    public Task<BerichtClassificatie> ClassificeerBerichtAsync(
        string body, string subject, string afzender,
        IReadOnlyList<ClassificatieCorrectieVoorbeeld>? voorbeelden = null)
        => _kern.ClassificeerBerichtAsync(body, subject, afzender, voorbeelden);

    /// <inheritdoc cref="BerichtAiKern.DetecteerCorrectieAsync"/>
    public Task<(bool IsCorrectie, string? AfgeleidJuistType, string? Samenvatting)> DetecteerCorrectieAsync(
        string body, string subject, string origineelType, string? originaleSamenvatting)
        => _kern.DetecteerCorrectieAsync(body, subject, origineelType, originaleSamenvatting);
}
