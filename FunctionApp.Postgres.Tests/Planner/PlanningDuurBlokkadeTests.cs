using System.Text.Json;
using AwesomeAssertions;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Planning;
using Xunit;

namespace FunctionApp.Postgres.Tests.Planner;

public class PlanningDuurBlokkadeTests
{
    [PostgresFact]
    public async Task AdminWrapper_ToontAlleenGetypeerdeDuurblokkadeAls409()
    {
        var originalBypass = Environment.GetEnvironmentVariable("LOCAL_AUTH_BYPASS");
        var originalSite = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        var originalConnection = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        try
        {
            Environment.SetEnvironmentVariable("LOCAL_AUTH_BYPASS", "true");
            Environment.SetEnvironmentVariable("WEBSITE_SITE_NAME", null);
            Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", PostgresTestEnvironment.ConnectionStringOrNull);
            AdminEndpoint.PoortGepasseerdVoorTests = null;
            var req = new DefaultHttpContext().Request;

            var blocked = await AdminEndpoint.ExecuteAsync(req, NullLogger.Instance, "planning",
                _ => throw new PlanningDuurOnbekendException());
            var generic = await AdminEndpoint.ExecuteAsync(req, NullLogger.Instance, "planning",
                _ => throw new InvalidOperationException("onverwachte fout"));

            var blockedResult = blocked.Should().BeOfType<ObjectResult>().Subject;
            blockedResult.StatusCode.Should().Be(409);
            JsonSerializer.Serialize(blockedResult.Value).Should().Contain(PlanningDuurOnbekendException.PubliekeMelding);
            var genericResult = generic.Should().BeOfType<ObjectResult>().Subject;
            genericResult.StatusCode.Should().Be(500);
            JsonSerializer.Serialize(genericResult.Value).Should().NotContain("onverwachte fout");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCAL_AUTH_BYPASS", originalBypass);
            Environment.SetEnvironmentVariable("WEBSITE_SITE_NAME", originalSite);
            Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", originalConnection);
            PostgresAppSettings.ResetForTests();
        }
    }
}
