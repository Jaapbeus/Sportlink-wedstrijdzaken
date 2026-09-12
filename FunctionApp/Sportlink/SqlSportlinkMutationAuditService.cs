using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// SQL Server-implementatie van audit-logging voor Sportlink-mutaties.
/// </summary>
internal sealed class SqlSportlinkMutationAuditService : ISportlinkMutationAuditService
{
    private readonly ILogger<SqlSportlinkMutationAuditService> _logger;

    public SqlSportlinkMutationAuditService(ILogger<SqlSportlinkMutationAuditService> logger)
    {
        _logger = logger;
    }

    public async Task<long> LogPogingAsync(SportlinkMutationAuditEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = new SqlCommand(@"
                INSERT INTO [dbo].[SportlinkMutationAudit]
                    ([ClubCode], [FunctioneleRol], [TriggerdDoor], [PublicMatchId], [Actie],
                     [WaardeVoor], [WaardeNa], [CorrelationId], [Resultaat], [Tijdstip])
                VALUES
                    (@ClubCode, @FunctioneleRol, @TriggerdDoor, @PublicMatchId, @Actie,
                     @WaardeVoor, @WaardeNa, @CorrelationId, 'Pending', GETUTCDATE());
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", connection);

            command.Parameters.AddWithValue("@ClubCode", entry.ClubCode ?? "");
            command.Parameters.AddWithValue("@FunctioneleRol", entry.FunctioneleRol ?? "");
            command.Parameters.AddWithValue("@TriggerdDoor", entry.TriggerdDoor ?? "");
            command.Parameters.AddWithValue("@PublicMatchId", entry.PublicMatchId ?? "");
            command.Parameters.AddWithValue("@Actie", entry.Actie ?? "");
            command.Parameters.AddWithValue("@WaardeVoor", (object?)entry.WaardeVoor ?? DBNull.Value);
            command.Parameters.AddWithValue("@WaardeNa", (object?)entry.WaardeNa ?? DBNull.Value);
            command.Parameters.AddWithValue("@CorrelationId", (object?)entry.CorrelationId ?? DBNull.Value);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is long id)
            {
                _logger.LogInformation("Sportlink mutation audit gelogd: ID {AuditId}, PublicMatchId {PublicMatchId}", id, entry.PublicMatchId);
                return id;
            }

            throw new InvalidOperationException("SCOPE_IDENTITY() gaf geen BIGINT terug");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij inloggen Sportlink mutation audit");
            throw;
        }
    }

    public async Task VoltooiAsync(long auditId, string resultaat, string? foutmeldingSamenvatting, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = new SqlCommand(@"
                UPDATE [dbo].[SportlinkMutationAudit]
                SET [Resultaat] = @Resultaat, [FoutmeldingSamenvatting] = @FoutmeldingSamenvatting
                WHERE [Id] = @Id", connection);

            command.Parameters.AddWithValue("@Id", auditId);
            command.Parameters.AddWithValue("@Resultaat", resultaat ?? "");
            command.Parameters.AddWithValue("@FoutmeldingSamenvatting", (object?)foutmeldingSamenvatting ?? DBNull.Value);

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected > 0)
                _logger.LogInformation("Sportlink mutation audit voltooId: ID {AuditId}, Resultaat {Resultaat}", auditId, resultaat);
            else
                _logger.LogWarning("Sportlink mutation audit niet gevonden: ID {AuditId}", auditId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij voltooiing Sportlink mutation audit");
            throw;
        }
    }
}
