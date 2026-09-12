using Database.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Regressietests voor #1004 en #1095: <see cref="PostgresConnectionStringNormalizer"/> negeerde
/// vóór #1004 elke <c>sslmode</c>-optie uit de URI-query en zette altijd <see cref="SslMode.Require"/>
/// — een modus die sinds Npgsql 8 geen certificaatketen of hostnaam meer valideert. #1004 maakte
/// dat fail-closed (exceptie zonder <c>verify-full</c>), wat bij release v3.3.0.0 de complete
/// productie-Function App platlegde omdat de bestaande instelling geen <c>sslmode</c> had. Sinds
/// #1095 is het beleid: versleuteld-maar-zwakker wordt <b>gemeld</b>, alleen expliciet onversleuteld
/// wordt geweigerd. Alle waarden hier zijn synthetisch (geen productie-hosts, -gebruikers of
/// -wachtwoorden).
/// </summary>
public class PostgresConnectionStringNormalizerTests
{
    // ---- #1004: URI met verify-full behoudt VerifyFull + juiste CA-configuratie, zonder waarschuwing ----

    [Fact]
    public void Normalize_UriMetVerifyFullEnSslRootCert_BehoudtBeideInstellingen()
    {
        var result = PostgresConnectionStringNormalizer.NormalizeWithDiagnostics(
            "postgresql://gebruiker:wachtwoord@db.voorbeeld.test:5432/sportlink?sslmode=verify-full&sslrootcert=synthetic-ca.pem");

        var builder = new NpgsqlConnectionStringBuilder(result.ConnectionString);
        builder.SslMode.Should().Be(SslMode.VerifyFull);
        builder.RootCertificate.Should().Be("synthetic-ca.pem");
        builder.Host.Should().Be("db.voorbeeld.test");
        result.EffectiveSslMode.Should().Be(SslMode.VerifyFull);
        result.TlsWarning.Should().BeNull();
    }

    [Fact]
    public void Normalize_UriMetVerifyFullZonderRootCert_BehoudtVerifyFullZonderWaarschuwing()
    {
        // VerifyFull steunend op de OS-truststore — voor een provider met publiek vertrouwde CA
        // voldoende; voor een provider met eigen CA is daarnaast sslrootcert nodig, maar dat is
        // een verbindingsfout op runtime, geen configuratiefout die de normalizer kan zien.
        var result = PostgresConnectionStringNormalizer.NormalizeWithDiagnostics(
            "postgresql://gebruiker:wachtwoord@db.voorbeeld.test:5432/sportlink?sslmode=verify-full");

        new NpgsqlConnectionStringBuilder(result.ConnectionString).SslMode.Should().Be(SslMode.VerifyFull);
        result.TlsWarning.Should().BeNull();
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

    // ---- #1095: niet-lokale host zonder verify-full → Require + waarschuwing, geen exceptie ----

    [Fact]
    public void Normalize_UriNaarNietLokaleHostZonderSslMode_ValtTerugOpRequireMetWaarschuwing()
    {
        // Exact het v3.3.0.0-incident: de productie-instelling in URI-vorm zonder ?sslmode=.
        // Vóór #1004 werd dit stilzwijgend Require; #1004 gooide; nu Require + zichtbare waarschuwing.
        var result = PostgresConnectionStringNormalizer.NormalizeWithDiagnostics(
            "postgresql://gebruiker:wachtwoord@db.voorbeeld.test:5432/sportlink");

        new NpgsqlConnectionStringBuilder(result.ConnectionString).SslMode.Should().Be(SslMode.Require);
        result.EffectiveSslMode.Should().Be(SslMode.Require);
        result.TlsWarning.Should().NotBeNullOrEmpty().And.Contain("verify-full");
    }

    [Fact]
    public void Normalize_TlsWaarschuwing_BevatGeenHostOfCredentials()
    {
        // /api/health is anoniem toegankelijk en toont deze tekst letterlijk.
        var result = PostgresConnectionStringNormalizer.NormalizeWithDiagnostics(
            "postgresql://geheimegebruiker:geheimwachtwoord@db.voorbeeld.test:5432/sportlink");

        result.TlsWarning.Should().NotBeNull();
        result.TlsWarning.Should().NotContain("db.voorbeeld.test");
        result.TlsWarning.Should().NotContain("geheimegebruiker");
        result.TlsWarning.Should().NotContain("geheimwachtwoord");
    }

    [Theory]
    [InlineData("prefer", SslMode.Require)]
    [InlineData("require", SslMode.Require)]
    [InlineData("verify-ca", SslMode.VerifyCA)]
    public void Normalize_UriNaarNietLokaleHostMetVersleuteldeMaarZwakkereSslMode_BehoudtVersleutelingMetWaarschuwing(
        string sslModeValue, SslMode verwacht)
    {
        var result = PostgresConnectionStringNormalizer.NormalizeWithDiagnostics(
            $"postgresql://gebruiker:wachtwoord@db.voorbeeld.test:5432/sportlink?sslmode={sslModeValue}");

        result.EffectiveSslMode.Should().Be(verwacht);
        new NpgsqlConnectionStringBuilder(result.ConnectionString).SslMode.Should().Be(verwacht);
        result.TlsWarning.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("allow")]
    public void Normalize_UriNaarNietLokaleHostMetOnversleuteldeSslMode_GooitException(string onversleuteld)
    {
        // Expliciet kiezen voor onversleuteld verkeer naar een productie-/stagingdatabase blijft een fout.
        var act = () => PostgresConnectionStringNormalizer.Normalize(
            $"postgresql://gebruiker:wachtwoord@db.voorbeeld.test:5432/sportlink?sslmode={onversleuteld}");

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

    // ---- URI- en keyword/value-vormen volgen hetzelfde beleid ----

    [Fact]
    public void Normalize_KeywordValueNaarNietLokaleHostZonderSslMode_ValtTerugOpRequireMetWaarschuwing()
    {
        // Vóór #1004 ging deze vorm ongewijzigd door (Npgsql-default Prefer); Require is strikt sterker.
        var result = PostgresConnectionStringNormalizer.NormalizeWithDiagnostics(
            "Host=db.voorbeeld.test;Port=5432;Database=sportlink;Username=gebruiker;Password=wachtwoord");

        result.EffectiveSslMode.Should().Be(SslMode.Require);
        result.TlsWarning.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Normalize_KeywordValueNaarNietLokaleHostMetVerifyFull_SlaagtEnBehoudtModus()
    {
        var result = PostgresConnectionStringNormalizer.NormalizeWithDiagnostics(
            "Host=db.voorbeeld.test;Port=5432;Database=sportlink;Username=gebruiker;Password=wachtwoord;SSL Mode=VerifyFull;Root Certificate=synthetic-ca.pem");

        var builder = new NpgsqlConnectionStringBuilder(result.ConnectionString);
        builder.SslMode.Should().Be(SslMode.VerifyFull);
        builder.RootCertificate.Should().Be("synthetic-ca.pem");
        result.TlsWarning.Should().BeNull();
    }

    [Fact]
    public void Normalize_KeywordValueNaarNietLokaleHostMetRequire_BehoudtRequireMetWaarschuwing()
    {
        var result = PostgresConnectionStringNormalizer.NormalizeWithDiagnostics(
            "Host=db.voorbeeld.test;Port=5432;Database=sportlink;Username=gebruiker;Password=wachtwoord;SSL Mode=Require");

        result.EffectiveSslMode.Should().Be(SslMode.Require);
        result.TlsWarning.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Normalize_KeywordValueNaarNietLokaleHostMetDisable_GooitException()
    {
        var act = () => PostgresConnectionStringNormalizer.Normalize(
            "Host=db.voorbeeld.test;Port=5432;Database=sportlink;Username=gebruiker;Password=wachtwoord;SSL Mode=Disable");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VerifyFull*");
    }

    // ---- Lokale ontwikkeling blijft expliciet werken (docker-compose.yml + docs/DEVELOPER-SETUP.md §7.2) ----

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public void Normalize_KeywordValueNaarLokaleHostZonderSslMode_SlaagtOnveranderdZonderWaarschuwing(string localHost)
    {
        // Exact de vorm die docs/DEVELOPER-SETUP.md §7.2 en de CI-job 'fresh-db-postgres'
        // gebruiken: geen sslmode opgegeven, moet blijven werken tegen de TLS-loze
        // docker-compose-container.
        var result = PostgresConnectionStringNormalizer.NormalizeWithDiagnostics(
            $"Host={localHost};Port=55432;Database=sportlink;Username=postgres;Password=devonly");

        var builder = new NpgsqlConnectionStringBuilder(result.ConnectionString);
        builder.Host.Should().Be(localHost);
        builder.SslMode.Should().Be(SslMode.Prefer);
        result.TlsWarning.Should().BeNull();
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
        // postgres-image zonder extra configuratie geen TLS. Npgsql's eigen default (Prefer)
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
