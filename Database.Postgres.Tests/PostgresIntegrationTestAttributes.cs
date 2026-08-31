using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Env-gestuurde vervanging van de vroegere onvoorwaardelijke <c>[Fact(Skip = "...")]</c>/
/// <c>[Theory(Skip = "...")]</c> op elke Postgres-integratietest (#866). Slaat zichzelf zichtbaar
/// over (met reden in de testuitvoer) wanneer <c>POSTGRES_TEST_CONNECTION_STRING</c> ontbreekt, en
/// draait onveranderd — zonder enige codewijziging — zodra die variabele gezet is. Werkt zowel
/// lokaal tegen een wegwerpcontainer als in CI (<c>fresh-db-postgres</c> in
/// <c>.github/workflows/build.yml</c>, die deze suite nu na de migratiestap ook daadwerkelijk
/// draait tegen de instantie die de job zelf al opzet).
/// <para>
/// Vervangt de individuele, letterlijk-gelijke Skip-strings die voorheen op elke integratietest
/// stonden — één plek voor de conditie in plaats van 23 herhalingen ervan.
/// </para>
/// <para>
/// <b>Let op — dit bestand wordt in TWEE testassemblies gecompileerd (issue 890):</b> ook
/// <c>FunctionApp.Postgres.Tests</c> neemt het op via <c>&lt;Compile Link&gt;</c>, zodat beide
/// suites dezelfde omgevingsvariabele en dezelfde skip-reden gebruiken en één CI-variabele ze
/// allebei aanzet. Een wijziging hier raakt dus beide. Een <c>ProjectReference</c> was geen optie:
/// het ene testproject naar het andere laten verwijzen levert dubbele testontdekking op.
/// </para>
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(PostgresTestEnvironment.ConnectionStringOrNull))
            Skip = PostgresTestEnvironment.SkipReason;
    }
}

/// <summary>Theory-tegenhanger van <see cref="PostgresFactAttribute"/> — zie die klasse.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(PostgresTestEnvironment.ConnectionStringOrNull))
            Skip = PostgresTestEnvironment.SkipReason;
    }
}

/// <summary>Gedeelde omgevingsvariabele-naam en -waarde, zodat de attributen en de testklassen zelf
/// (die de connection string ook nodig hebben om daadwerkelijk te verbinden) niet uit de pas kunnen
/// lopen.</summary>
public static class PostgresTestEnvironment
{
    public const string ConnectionStringEnvVar = "POSTGRES_TEST_CONNECTION_STRING";

    public const string SkipReason =
        "Vereist " + ConnectionStringEnvVar + " — lokaal uitvoeren tegen een wegwerpcontainer " +
        "(zie PostgresMergeOrchestratorIntegrationTests-klasse-doc-comment) of in CI " +
        "(fresh-db-postgres in .github/workflows/build.yml).";

    public static string? ConnectionStringOrNull => Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
}
