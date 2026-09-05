using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres.Integrations.SportlinkClub;

/// <summary>
/// Productie-tokenbeheer voor de Sportlink Club Keycloak-realm (#990/#991, epic #986). Elke
/// aanroep doet een verse <c>refresh_token</c>-grant en schrijft het geroteerde token meteen terug
/// in <c>public.sportlinkservicetokens</c> — bewust géén in-memory caching van het access_token:
/// Azure Functions Consumption-instances zijn kortlevend, dus hergebruik tussen aanroepen is niet
/// betrouwbaar. Zelfde patroon als het al gevalideerde <c>scripts/dev/Invoke-SportlinkTokenSpike.ps1</c>.
/// <para>
/// Tokenopslag in een eigen DB-tabel i.p.v. Key Vault (kost geld, nieuwe Azure-resource) of een
/// Function App Setting via ARM-API (vereist een aparte Azure AD-integratie met schrijfrechten op
/// de eigen Function App) — besluit vastgelegd in
/// docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §6 / issue #990.
/// </para>
/// </summary>
public static class SportlinkClubTokenProvider
{
    private const string TokenEndpoint = "https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/token";

    // Sportlink Club Web's eigen publieke OAuth-client-id — geen geheim, geen clubdata (#990-onderzoek).
    private const string ClientId = "sportlink-club-web";

    /// <summary>Haalt een geldig access_token op voor de gegeven rol/club, en roteert het
    /// opgeslagen refresh_token. Gooit <see cref="SportlinkNietGekoppeldException"/> als er nog
    /// geen koppeling is, of <see cref="SportlinkTokenVerlopenException"/> als Sportlink het
    /// opgeslagen refresh_token weigert.</summary>
    public static async Task<string> GetAccessTokenAsync(
        string connectionString, string rolNaam, string clubCode, HttpClient tokenHttp, ILogger log)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var huidigToken = await LeesRefreshTokenAsync(connection, rolNaam, clubCode);
        if (huidigToken == null)
            throw new SportlinkNietGekoppeldException(rolNaam);

        var response = await DoeRefreshAsync(tokenHttp, huidigToken, log);
        if (response == null)
            throw new SportlinkTokenVerlopenException(rolNaam);

        await SchrijfRefreshTokenAsync(connection, rolNaam, clubCode, response.RefreshToken, response.RefreshExpiresIn);
        return response.AccessToken;
    }

    /// <summary>Bootstrap: valideert een nieuw refresh_token met één refresh-poging vóórdat het
    /// opgeslagen wordt — voorkomt dat een ongeldige waarde onopgemerkt in de tabel belandt.
    /// Gebruikt door de admin-PUT (<c>SportlinkExtensieRollenFunction</c>).</summary>
    public static async Task RegisterInitialTokenAsync(
        string connectionString, string rolNaam, string clubCode, string refreshToken, HttpClient tokenHttp, ILogger log)
    {
        var response = await DoeRefreshAsync(tokenHttp, refreshToken, log)
            ?? throw new SportlinkTokenVerlopenException(rolNaam);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SchrijfRefreshTokenAsync(connection, rolNaam, clubCode, response.RefreshToken, response.RefreshExpiresIn);
    }

    private static async Task<string?> LeesRefreshTokenAsync(NpgsqlConnection connection, string rolNaam, string clubCode)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT refreshtoken FROM public.sportlinkservicetokens WHERE rolnaam = @rolnaam AND clubcode = @clubcode",
            connection);
        cmd.Parameters.AddWithValue("rolnaam", rolNaam);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    private static async Task SchrijfRefreshTokenAsync(
        NpgsqlConnection connection, string rolNaam, string clubCode, string refreshToken, int refreshExpiresInSeconds)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.sportlinkservicetokens (rolnaam, clubcode, refreshtoken, refreshtokenvervaltop, bijgewerktop)
            VALUES (@rolnaam, @clubcode, @refreshtoken, @vervaltop, now())
            ON CONFLICT (rolnaam, clubcode) DO UPDATE SET
                refreshtoken = @refreshtoken, refreshtokenvervaltop = @vervaltop, bijgewerktop = now()",
            connection);
        cmd.Parameters.AddWithValue("rolnaam", rolNaam);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("refreshtoken", refreshToken);
        cmd.Parameters.AddWithValue("vervaltop", DateTime.UtcNow.AddSeconds(refreshExpiresInSeconds));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Retourneert <c>null</c> bij <c>invalid_grant</c> (verlopen/ingetrokken token) —
    /// onderscheiden van een echte netwerk-/serverfout, die als exception doorgooit.</summary>
    private static async Task<SportlinkTokenResponse?> DoeRefreshAsync(HttpClient tokenHttp, string refreshToken, ILogger log)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
        });

        using var httpResponse = await tokenHttp.PostAsync(TokenEndpoint, body);
        if (httpResponse.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Geen tokenwaarde loggen — alleen dat de refresh geweigerd is.
            log.LogWarning("Sportlink refresh_token geweigerd (invalid_grant of vergelijkbaar).");
            return null;
        }
        httpResponse.EnsureSuccessStatusCode();

        await using var stream = await httpResponse.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<SportlinkTokenResponse>(stream);
    }
}
