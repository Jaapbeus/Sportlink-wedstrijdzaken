using Xunit;

namespace FunctionApp.Tests.Feedback;

/// <summary>
/// Slaat een test over zonder <c>SQLSERVER_TEST_CONNECTION_STRING</c> (#764). Er is op de SQL
/// Server-tier geen CI-job met een levende database voor unit-tests; deze tests draai je lokaal
/// tegen een wegwerpcontainer (zie klasse-doc-comment van <c>SqlFeedbackStoreIntegrationTests</c>).
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string EnvVar = "SQLSERVER_TEST_CONNECTION_STRING";

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Vereist {EnvVar} — lokaal tegen een wegwerpcontainer met het avg-schema uit Database/Script.PostDeployment1.sql.";
    }
}
