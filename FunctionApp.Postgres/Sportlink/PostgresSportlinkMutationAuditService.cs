using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Postgres-implementatie van audit-logging voor Sportlink-mutaties.
/// </summary>
internal sealed class PostgresSportlinkMutationAuditService : ISportlinkMutationAuditService
{
    private readonly ILogger<PostgresSportlinkMutationAuditService> _logger;

    public PostgresSportlinkMutationAuditService(ILogger<PostgresSportlinkMutationAuditService> logger)
    {
        _logger = logger;
    }

    public async Task<long> LogPogingAsync(SportlinkMutationAuditEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO public.sportlinkmutationaudit
                    (clubcode, functionelerol, triggerddoor, publicmatchid, actie,
                     waardevoor, waardena, correlationid, resultaat, tijdstip)
                VALUES
                    (@clubcode, @functionelerol, @triggerddoor, @publicmatchid, @actie,
                     @waardevoor, @waardena, @correlationid, 'Pending', now())
                RETURNING id;";

            command.Parameters.AddWithValue("@clubcode", entry.ClubCode ?? "");
            command.Parameters.AddWithValue("@functionelerol", entry.FunctioneleRol ?? "");
            command.Parameters.AddWithValue("@triggerddoor", entry.TriggerdDoor ?? "");
            command.Parameters.AddWithValue("@publicmatchid", entry.PublicMatchId ?? "");
            command.Parameters.AddWithValue("@actie", entry.Actie ?? "");
            command.Parameters.AddWithValue("@waardevoor", (object?)entry.WaardeVoor ?? DBNull.Value);
            command.Parameters.AddWithValue("@waardena", (object?)entry.WaardeNa ?? DBNull.Value);
            command.Parameters.AddWithValue("@correlationid", (object?)entry.CorrelationId ?? DBNull.Value);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is long id)
            {
                _logger.LogInformation("Sportlink mutation audit gelogd: ID {AuditId}, PublicMatchId {PublicMatchId}", id, entry.PublicMatchId);
                return id;
            }

            throw new InvalidOperationException("RETURNING id gaf geen waarde terug");
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
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE public.sportlinkmutationaudit
                SET resultaat = @resultaat, foutmeldingsamenvatting = @foutmeldingsamenvatting
                WHERE id = @id";

            command.Parameters.AddWithValue("@id", auditId);
            command.Parameters.AddWithValue("@resultaat", resultaat ?? "");
            command.Parameters.AddWithValue("@foutmeldingsamenvatting", (object?)foutmeldingSamenvatting ?? DBNull.Value);

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected > 0)
                _logger.LogInformation("Sportlink mutation audit voltooid: ID {AuditId}, Resultaat {Resultaat}", auditId, resultaat);
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
