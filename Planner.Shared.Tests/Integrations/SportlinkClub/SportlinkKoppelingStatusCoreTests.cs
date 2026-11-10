using AwesomeAssertions;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

/// <summary>#1773: statusbepaling voor de vooraf-melding op de werkpagina's. Alle waarden zijn fictief.</summary>
public class SportlinkKoppelingStatusCoreTests
{
    private const string Rol = "Wedstrijdzaken";
    private static readonly DateTimeOffset Nu = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    // Alleen de aanwezigheid van credentials telt; de waarden worden door de statusbepaling nooit gelezen.
    private static readonly SportlinkLoginCredentials Credentials = new()
    {
        Username = "service-account-fixture", Password = "fixture-wachtwoord", TotpSecret = "fixture-sleutel",
    };

    [Fact]
    public void Geen_state_en_geen_refresh_token_is_verbroken_niet_gekoppeld()
    {
        var s = SportlinkKoppelingStatusCore.Bepaal(Rol, null, refreshTokenAanwezig: false, Nu);

        s.Niveau.Should().Be(SportlinkKoppelingNiveau.Verbroken);
        s.IsVerbroken.Should().BeTrue();
        s.Melding.Should().Contain("Geen Sportlink-koppeling").And.Contain(Rol);
    }

    [Fact]
    public void Alleen_refresh_token_zonder_credentials_is_ok()
    {
        var s = SportlinkKoppelingStatusCore.Bepaal(Rol, null, refreshTokenAanwezig: true, Nu);

        s.Niveau.Should().Be(SportlinkKoppelingNiveau.Ok);
        s.Melding.Should().BeNull();
    }

    [Fact]
    public void Gezonde_state_is_ok_en_geeft_laatste_login_door()
    {
        var state = new SportlinkAutoLoginState { Credentials = Credentials, LastLoginUtc = Nu.AddHours(-2), FailureCount = 0 };

        var s = SportlinkKoppelingStatusCore.Bepaal(Rol, state, refreshTokenAanwezig: true, Nu);

        s.Niveau.Should().Be(SportlinkKoppelingNiveau.Ok);
        s.LaatsteLoginUtc.Should().Be(Nu.AddHours(-2));
    }

    [Fact]
    public void Retrydatum_jaar_9999_is_verbroken_geblokkeerd()
    {
        var state = new SportlinkAutoLoginState
        {
            Credentials = Credentials, LastLoginUtc = Nu.AddDays(-2), FailureCount = 1,
            RetryAfterUtc = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero), LastError = "Controleer logininstellingen",
        };

        var s = SportlinkKoppelingStatusCore.Bepaal(Rol, state, refreshTokenAanwezig: true, Nu);

        s.Niveau.Should().Be(SportlinkKoppelingNiveau.Verbroken);
        s.Melding.Should().Contain("geblokkeerd");
    }

    [Fact]
    public void Drie_mislukkingen_is_verbroken_ook_zonder_retrydatum()
    {
        var state = new SportlinkAutoLoginState { Credentials = Credentials, FailureCount = 3 };

        SportlinkKoppelingStatusCore.Bepaal(Rol, state, true, Nu).Niveau.Should().Be(SportlinkKoppelingNiveau.Verbroken);
    }

    [Fact]
    public void Tijdelijke_mislukking_met_recente_login_is_waarschuwing()
    {
        var state = new SportlinkAutoLoginState
        {
            Credentials = Credentials, LastLoginUtc = Nu.AddHours(-1), FailureCount = 1, RetryAfterUtc = Nu.AddMinutes(10),
        };

        var s = SportlinkKoppelingStatusCore.Bepaal(Rol, state, refreshTokenAanwezig: false, Nu);

        s.Niveau.Should().Be(SportlinkKoppelingNiveau.Waarschuwing);
        s.Melding.Should().Contain("tijdelijk");
    }

    [Fact]
    public void Tijdelijke_mislukking_na_sessieduur_zonder_refresh_token_is_verbroken()
    {
        var state = new SportlinkAutoLoginState
        {
            Credentials = Credentials, LastLoginUtc = Nu.AddHours(-11), FailureCount = 1, RetryAfterUtc = Nu.AddMinutes(10),
        };

        var s = SportlinkKoppelingStatusCore.Bepaal(Rol, state, refreshTokenAanwezig: false, Nu);

        s.Niveau.Should().Be(SportlinkKoppelingNiveau.Verbroken);
        s.Melding.Should().Contain("verlopen");
    }

    [Fact]
    public void Verstreken_retrydatum_zonder_blokkade_is_ok()
    {
        var state = new SportlinkAutoLoginState
        {
            Credentials = Credentials, LastLoginUtc = Nu.AddHours(-1), FailureCount = 1, RetryAfterUtc = Nu.AddMinutes(-1),
        };

        SportlinkKoppelingStatusCore.Bepaal(Rol, state, true, Nu).Niveau.Should().Be(SportlinkKoppelingNiveau.Ok);
    }

    [Fact]
    public void Melding_bevat_nooit_credentialwaarden()
    {
        var state = new SportlinkAutoLoginState { Credentials = Credentials, FailureCount = 3, LastError = "Controleer logininstellingen" };

        var s = SportlinkKoppelingStatusCore.Bepaal(Rol, state, true, Nu);

        s.Melding.Should().NotContain(Credentials.Username).And.NotContain(Credentials.TotpSecret).And.NotContain(Credentials.Password);
    }
}
