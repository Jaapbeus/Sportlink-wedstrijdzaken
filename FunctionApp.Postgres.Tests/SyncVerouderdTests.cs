using FluentAssertions;
using FunctionApp.Postgres;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #1081: de beslisregel achter <c>syncStale</c> in <c>/api/health</c>.
///
/// <para>
/// Deze regel bestaat omdat #1077 acht dagen onopgemerkt bleef: de synchronisatie draaide elke
/// nacht, werkte niets bij, en niets meldde dat. De regel is hier bewust losgetrokken van database
/// en omgeving, zodat het oordeel zelf toetsbaar is zonder draaiende infrastructuur.
/// </para>
/// </summary>
public class SyncVerouderdTests
{
    private static readonly DateTime Nu = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NooitGesynchroniseerd_TeltAlsVerouderd()
        => HealthFunction.IsSyncVerouderd(null, Nu, 36).Should().BeTrue(
            "waar een synchronisatie hoort te draaien is 'nog nooit gedraaid' een storing, geen neutrale begintoestand");

    [Fact]
    public void RecenteSync_IsNietVerouderd()
        => HealthFunction.IsSyncVerouderd(Nu.AddHours(-10), Nu, 36).Should().BeFalse();

    [Fact]
    public void EenGemisteNachtelijkeRun_IsVerouderd()
        => HealthFunction.IsSyncVerouderd(Nu.AddHours(-40), Nu, 36).Should().BeTrue(
            "de timer draait dagelijks, dus 40 uur betekent dat een run is overgeslagen of mislukt");

    [Fact]
    public void EenRunDieWatUitloopt_IsNietVerouderd()
        => HealthFunction.IsSyncVerouderd(Nu.AddHours(-30), Nu, 36).Should().BeFalse(
            "de drempel ligt bewust boven 24 uur zodat een latere of langer durende run geen vals alarm geeft");

    [Fact]
    public void PreciesOpDeDrempel_IsNogNietVerouderd()
        => HealthFunction.IsSyncVerouderd(Nu.AddHours(-36), Nu, 36).Should().BeFalse(
            "de vergelijking is strikt groter dan — gelijk aan de drempel is nog binnen de marge");

    /// <summary>
    /// De acht dagen uit #1077, als regressiegeval: precies deze situatie hoorde zichzelf te melden.
    /// </summary>
    [Fact]
    public void AchtDagenOud_IsVerouderd()
        => HealthFunction.IsSyncVerouderd(Nu.AddDays(-8), Nu, 36).Should().BeTrue();
}
