using System.Runtime.CompilerServices;
using Planner.Shared.Email;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// De reviewmodus is fail-safe (#1809): zonder expliciete waarde staat hij AAN. Deze tests verwachten antwoorden
/// zonder review-kop, dus de testomgeving zet hem expliciet uit; tests die de modus willen zien zetten hem zelf.
/// </summary>
internal static class ReviewModusTestOmgeving
{
    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable(EmailReviewModus.InstellingNaam, "false");
}
