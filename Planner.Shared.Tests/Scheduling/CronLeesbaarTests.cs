using Planner.Shared.Scheduling;
using Xunit;

namespace Planner.Shared.Tests.Scheduling;

// #1623: één gedeelde cron-naar-tekstvertaler voor beide tiers en de Blazor-instellingenpagina.
public class CronLeesbaarTests
{
    [Theory]
    [InlineData("0 0 4 * * *", "Elke dag om 04:00")]            // voorbeeldtekst uit openapi.yaml
    [InlineData("0 0 0 * * *", "Elke dag om 00:00")]
    [InlineData("0 30 6 * * *", "Elke dag om 06:30")]
    [InlineData("0 0 * * * *", "Elk uur")]
    [InlineData("0 15 * * * *", "Elk uur op minuut :15")]
    [InlineData("0 */15 * * * *", "Elke 15 minuten")]
    [InlineData("0 30 2 1 * *", "Maandelijks op dag 1 om 02:30")]
    [InlineData("0 0 8 * * 1", "Elke maandag om 08:00")]
    [InlineData("0 0 8 * * 0", "Elke zondag om 08:00")]
    [InlineData("0 0 8 * * 7", "Elke zondag om 08:00")]
    [InlineData("0 0 8 * * 5", "Elke vrijdag om 08:00")]
    [InlineData("  0 0 4 * * *  ", "Elke dag om 04:00")]
    public void Vertaal_HerkentPatroon(string cron, string verwacht)
        => Assert.Equal(verwacht, CronLeesbaar.Vertaal(cron));

    [Theory]
    [InlineData("0 0 8 * * 8")]        // weekdag buiten 0-7
    [InlineData("0 0 4 * *")]          // 5 velden
    [InlineData("0 0 x * * *")]        // niet-numeriek uur
    [InlineData("0 */0 * * * *")]      // interval 0
    [InlineData("5 0 4 * * *")]        // seconden ongelijk aan 0
    [InlineData("0 0 4 * 6 *")]        // maand ingevuld
    public void Vertaal_OnbekendPatroon_GeeftRuweExpressieTerug(string cron)
        => Assert.Equal(cron, CronLeesbaar.Vertaal(cron));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Vertaal_LegeInvoer_GeeftLegeTekst(string? cron)
        => Assert.Equal("", CronLeesbaar.Vertaal(cron));
}
