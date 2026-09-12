using Database.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Regressietests voor #1004: <see cref="PostgresConnectionStringNormalizer"/> negeerde voorheen
/// elke <c>sslmode</c>-optie uit de URI-query en zette altijd <see cref="SslMode.Require"/> — een
/// modus die sinds Npgsql 8 geen certificaatketen of hostnaam meer valideert. Alle waarden hier
/// zijn synthetisch (geen productie-hosts, -gebruikers of -wachtwoorden).
/// </summary>
public class PostgresConnectionStringNormalizerTests
{
    // ---- Acceptatiecriterium: URI met verify-full behoudt VerifyFull + juiste CA-configuratie ----

    [Fact]
    public void Normalize_UriMetVerifyFullEnSslRootCert_BehoudtBeideInstellingen()
    {
        var result = PostgresConnectionStringNormalizer.Normalize(
            "postgresql://gebruiker:wachtwoord@db.voorbeeld.test:5432/sportlink?sslmode=verify-full&sslrootcert=synthetic-ca.pem");

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.SslMode.Should().Be(SslMode.VerifyFull);
        builder.RootCertificate.Should().Be("synthetic-ca.pem");
        builder.Host.Should().Be("db.voorbeeld.test");
    }

    [Fact]
    public void Normalize_UriMetVerifyFullZonderRootCert_BehoudtVerifyFull()
    {
        // Publiek vertrouwde CA (bv. Supabase) heeft geen los root-certificaat nodig — VerifyFull
        // alleen (steunend op de OS-truststore) is dan al voldoende en moet blijven werken.
        var result = PostgresConnectionStringNormalizer.Normalize(
            "postgresql://gebruiker:wachtwoord@db.voorbeeld.test:5432/sportlink?sslmode=verify-full");

        new NpgsqlConnectionStringBuilder(result).SslMode.Should().Be(SslMode.VerifyFull);
    }

    [Theory]
    [InlineData("verify-full")]
    [InlineData("VERIFY-FULL")]
    [InlineData("Verify-Full")]
    public void Normalize_SslModeIsCaseInsensitive(string sslModeValue)
    {
        var result = PostgresConnectionStringNormalizer.Normalize(
            $"postgresql://gebruiker:wachtwoord@db.voorbeeld.test:5432/sportlink?sslmode={sslModeValue}");

        new NpgsqlConnectionStringBuilder(result).SslMode.Should().Be(SslMode.VerifyFull);
    }

    // ---- Acceptatiecriterium: productieconfiguratie met zwakkere modus wordt geweigerd vóór de eerste verbinding ----

    [Fact]
    public void Normalize_UriNaarNietLokaleHostZonderSslMode_GooitException()
    {
        var act = () => PostgresConnectionStringNormalizer.Normalize(
            "postgresql://gebruiker:wachtwoord@db.voorbeeld.test:5432/sportlink");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VerifyFull*");
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("allow")]
    [InlineData("prefer")]
    [InlineData("require")]
    [InlineData("verify-ca")]
    public void Normalize_UriNaarNietLokaleHostMetZwakkereSslMode_GooitException(string zwakkeMode)
    {
        var act = () => PostgresConnectionStringNormalizer.Normalize(
            $"postgresql://gebruiker:wachtwoord@db.voorbeeld.test:5432/sportlink?sslmode={zwakkeMode}");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VerifyFull*");
    }

    [Fact]
    public void Normalize_OnbekendeSslModeWaarde_GooitException()
    {
        var act = () => PostgresConnectionStringNormalizer.Normalize(
            "postgresql://gebruiker:wachtwoord@db.voorbeeld.test:5432/sportlink?sslmode=onzin");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Normalize_SslRootCertZonderPassendeSslMode_GooitException()
    {
        // Tegenstrijdig: een root-CA is opgegeven, maar bij 'require' valideert Npgsql de keten
        // toch niet — het certificaat zou stilzwijgend genegeerd worden.
        var act = () => PostgresConnectionStringNormalizer.Normalize(
            "postgresql://gebruiker:wachtwoord@db.voorbeeld.test:5432/sportlink?sslmode=require&sslrootcert=synthetic-ca.pem");

        act.Should().Throw<InvalidOperationException>();
    }

    // ---- Acceptatiecriterium: URI- en keyword/value-vormen volgen hetzelfde beleid ----

    [Fact]
    public void Normalize_KeywordValueNaarNietLokaleHostZonderSslMode_GooitException()
    {
        var act = () => PostgresConnectionStringNormalizer.Normalize(
            "Host=db.voorbeeld.test;Port=5432;Database=sportlink;Username=gebruiker;Password=wachtwoord");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VerifyFull*");
    }

    [Fact]
    public void Normalize_KeywordValueNaarNietLokaleHostMetVerifyFull_SlaagtEnBehoudtModus()
    {
        var result = PostgresConnectionStringNormalizer.Normalize(
            "Host=db.voorbeeld.test;Port=5432;Database=sportlink;Username=gebruiker;Password=wachtwoord;SSL Mode=VerifyFull;Root Certificate=synthetic-ca.pem");

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.SslMode.Should().Be(SslMode.VerifyFull);
        builder.RootCertificate.Should().Be("synthetic-ca.pem");
    }

    [Fact]
    public void Normalize_KeywordValueNaarNietLokaleHostMetZwakkereSslMode_GooitException()
    {
        var act = () => PostgresConnectionStringNormalizer.Normalize(
            "Host=db.voorbeeld.test;Port=5432;Database=sportlink;Username=gebruiker;Password=wachtwoord;SSL Mode=Require");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VerifyFull*");
    }

    // ---- Lokale ontwikkeling blijft expliciet werken (docker-compose.yml + docs/DEVELOPER-SETUP.md §7.2) ----

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public void Normalize_KeywordValueNaarLokaleHostZonderSslMode_SlaagtOnveranderd(string localHost)
    {
        // Exact de vorm die docs/DEVELOPER-SETUP.md §7.2 en de CI-job 'fresh-db-postgres'
        // gebruiken: geen sslmode opgegeven, moet blijven werken tegen de TLS-loze
        // docker-compose-container.
        var result = PostgresConnectionStringNormalizer.Normalize(
            $"Host={localHost};Port=55432;Database=sportlink;Username=postgres;Password=devonly");

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.Host.Should().Be(localHost);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public void Normalize_UriNaarLokaleHostZonderSslQuery_SlaagtEnZetGeenGeforceerdeSslMode(string localHost)
    {
        var result = PostgresConnectionStringNormalizer.Normalize(
            $"postgresql://postgres:devonly@{localHost}:55432/sportlink");

        var builder = new NpgsqlConnectionStringBuilder(result);
        // Vóór #1004 werd hier altijd SslMode.Require geforceerd, ook al ondersteunt de officiële
        // postgres:16-image zonder extra configuratie geen TLS. Npgsql's eigen default (Prefer)
        // valt terug op onversleuteld, precies zoals de lokale workflow vandaag al werkt.
        builder.SslMode.Should().Be(SslMode.Prefer);
    }

    // ---- Bestaande parsinggedrag (percent-encoding, standaardpoort) blijft groen ----

    [Fact]
    public void Normalize_UriMetPercentEncodedLoginvelden_DecodeertGebruikersnaamEnWachtwoord()
    {
        var result = PostgresConnectionStringNormalizer.Normalize(
            "postgresql://ge%40bruiker:wacht%23woord@localhost:5432/sportlink");

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.Username.Should().Be("ge@bruiker");
        builder.Password.Should().Be("wacht#woord");
    }

    [Fact]
    public void Normalize_UriZonderExplicietePoort_GebruiktStandaardpoort5432()
    {
        var result = PostgresConnectionStringNormalizer.Normalize(
            "postgresql://gebruiker:wachtwoord@localhost/sportlink");

        new NpgsqlConnectionStringBuilder(result).Port.Should().Be(5432);
    }

    [Fact]
    public void Normalize_UriZonderPad_GebruiktPostgresAlsStandaarddatabase()
    {
        var result = PostgresConnectionStringNormalizer.Normalize(
            "postgresql://gebruiker:wachtwoord@localhost:5432/");

        new NpgsqlConnectionStringBuilder(result).Database.Should().Be("postgres");
    }

    [Fact]
    public void Normalize_KeywordValueVormOnveranderdAlsGeenPostgresUri()
    {
        // Geen postgres://-of postgresql://-prefix -> altijd al de keyword/value-tak, ongeacht
        // welke host erin staat; dit bewijst dat de vormdetectie zelf niet is veranderd.
        var act = () => PostgresConnectionStringNormalizer.Normalize(
            "Host=localhost;Port=55432;Database=sportlink;Username=postgres;Password=devonly");

        act.Should().NotThrow();
    }
}
