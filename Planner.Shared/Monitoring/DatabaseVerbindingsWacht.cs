using System.Data.Common;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Planner.Shared.Monitoring;

/// <summary>Gedeelde retries voor uitsluitend het openen van een databaseverbinding.</summary>
public static class DatabaseVerbindingsWacht
{
    public static Task WachtAsync(Func<DbConnection> maakVerbinding, int pogingen,
        int vertragingMs, ILogger log, Action? naOpen = null) => WachtAsync(async () =>
    {
        await using var verbinding = maakVerbinding();
        await verbinding.OpenAsync();
        naOpen?.Invoke();
    }, pogingen, vertragingMs, log);

    public static async Task WachtAsync(Func<Task> openVerbinding, int pogingen, int vertragingMs, ILogger log)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pogingen, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(vertragingMs);
        for (var poging = 1; poging <= pogingen; poging++)
        {
            try
            {
                await openVerbinding();
                log.LogInformation("Database connection established.");
                return;
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or IOException or SocketException)
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
