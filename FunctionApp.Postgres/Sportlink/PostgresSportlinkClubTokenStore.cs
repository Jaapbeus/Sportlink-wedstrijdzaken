using FunctionApp.Postgres.Planner;
using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Postgres-tier implementatie van <see cref="ISportlinkClubTokenStore"/> (#991, epic #986) —
/// bewaart het rotarende refresh_token in een eigen DB-tabel (<c>public.sportlinkservicetokens</c>)
/// in plaats van <see cref="Planner.Shared.Integrations.SportlinkClub.SportlinkClubAppSettingsTokenStore"/>
/// (Function App-instelling via de Azure Management API, #998). Besluit: geen nieuwe Azure-resource
/// (Key Vault) en geen aparte Azure AD-integratie met schrijfrechten op de eigen Function App
/// (ARM-API) — een bestaande, gratis DB-tabel volstaat en heeft dezelfde vertrouwensgrens als de
/// bestaande <c>SqlConnectionString</c>-secrets. Zie docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md
/// §6 / issue #990.
/// <para>
/// <b><c>LeesRefreshToken</c> is synchroon</b> omdat <see cref="ISportlinkClubTokenStore"/> dat
/// voorschrijft (gedeelde interface in <c>Planner.Shared</c>, niet hier aan te passen zonder de
/// SQL Server-tier's implementatie te raken). <c>.GetAwaiter().GetResult()</c> is hier veilig: de
/// Azure Functions isolated worker host heeft geen <c>SynchronizationContext</c> (thread-pool-
/// gebaseerd), dus er is geen deadlock-risico zoals bij ASP.NET (Core) met een sync-context.
/// </para>
/// </summary>
public class PostgresSportlinkClubTokenStore : ISportlinkClubTokenStore
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresSportlinkClubTokenStore> _logger;

    /// <summary>
    /// <paramref name="connectionString"/> is een expliciete parameter (i.p.v. rechtstreeks
    /// <c>PostgresDatabaseConfig.ConnectionString</c> lezen) zodat een test een lokale
    /// testdatabase kan meegeven — zie de DI-registratie in <c>Program.cs</c> voor de
    /// productiewaarde.
    /// </summary>
    public PostgresSportlinkClubTokenStore(string connectionString, ILogger<PostgresSportlinkClubTokenStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public string? LeesRefreshToken(string functioneleRol)
        => LeesRefreshTokenAsync(functioneleRol).GetAwaiter().GetResult();

    private async Task<string?> LeesRefreshTokenAsync(string functioneleRol)
    {
        var clubCode = PostgresClubScope.Primary;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT refreshtoken FROM public.sportlinkservicetokens WHERE rolnaam = @rolnaam AND clubcode = @clubcode",
            connection);
        cmd.Parameters.AddWithValue("rolnaam", functioneleRol);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    public async Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default)
    {
        var clubCode = PostgresClubScope.Primary;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // expires_in van de refresh-grant zelf staat niet in deze interface — de token-rotatie in
        // SportlinkClubClient roept dit pas aan ná een geslaagde refresh, dus 6 uur (het bevestigde
        // refresh_expires_in bij eerste uitgifte, zie onderzoeksrapport §2.6) is een behoudende
        // ondergrens, niet de daadwerkelijke geldigheidsduur.
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.sportlinkservicetokens (rolnaam, clubcode, refreshtoken, refreshtokenvervaltop, bijgewerktop)
            VALUES (@rolnaam, @clubcode, @refreshtoken, now() + interval '6 hours', now())
            ON CONFLICT (rolnaam, clubcode) DO UPDATE SET
                refreshtoken = @refreshtoken, refreshtokenvervaltop = now() + interval '6 hours', bijgewerktop = now()",
            connection);
        cmd.Parameters.AddWithValue("rolnaam", functioneleRol);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("refreshtoken", nieuwRefreshToken);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Refresh-token voor rol '{Rol}' geroteerd en opgeslagen.", functioneleRol);
    }
}
