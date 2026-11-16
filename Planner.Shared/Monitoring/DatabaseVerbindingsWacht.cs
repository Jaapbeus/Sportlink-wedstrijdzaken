using Microsoft.Extensions.Logging;

namespace Planner.Shared.Monitoring;

/// <summary>Gedeelde retries voor uitsluitend het openen van een databaseverbinding.</summary>
public static class DatabaseVerbindingsWacht
{
    public static async Task WachtAsync(Func<Task> openVerbinding, int pogingen, int vertragingMs, ILogger log)
    {
        for (var poging = 1; poging <= pogingen; poging++)
        {
            try
            {
                await openVerbinding();
                log.LogInformation("Database connection established.");
                return;
            }
            catch (Exception)
            {
                log.LogWarning("Database connection failed. Retry {Attempt}/{MaxRetries}", poging, pogingen);
                if (poging < pogingen) await Task.Delay(vertragingMs);
            }
        }

        throw new DatabaseVerbindingOnbeschikbaarException();
    }
}

public sealed class DatabaseVerbindingOnbeschikbaarException()
    : Exception("Unable to establish a database connection after multiple attempts.");
