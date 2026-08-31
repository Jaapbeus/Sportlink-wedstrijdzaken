using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using SportlinkFunction.TeamResolution;

namespace SportlinkFunction.Admin;

/// <summary>
/// <c>POST /api/beheer/teams/herstel</c> — bouwt de canonieke teamlijst opnieuw op uit
/// <c>his.Teams</c> (#946).
///
/// <para>
/// <b>Waarom dit bestaat.</b> <c>dbo.Teams</c>/<c>dbo.TeamAliassen</c> zijn afgeleide tabellen; ze
/// worden gevuld aan het eind van elke synchronisatie. Staat die uit (<c>syncEnabled = 0</c>), of
/// heeft die sinds de uitrol nog niet gelopen, dan kon een beheerder niets doen behalve wachten: de
/// teamkeuzelijst bleef leeg, <c>team-schedule</c> gaf 404, en sinds #945 meldt de
/// beschikbaarheidscontrole eerlijk dat zij niets kón controleren — maar dat maakt het nog niet
/// herstelbaar. Dit endpoint is die hendel.
/// </para>
///
/// <para>
/// <b>Onvoorwaardelijk, anders dan <see cref="TeamlijstGereedheid"/>.</b> Die klasse hangt aan het
/// e-mailpad en vult alleen aan wanneer de lijst leeg is. Hier heeft iemand expliciet om herstel
/// gevraagd, dus draaien altijd beide stappen: de volledige canonicalisatie én de sleutelmigratie
/// (#766), die nodig is wanneer de opgeslagen sleutels nog volgens oudere normalisatieregels
/// berekend zijn. Juist dat laatste geval kwam voorheen alleen aan bod als er toevallig e-mail
/// verwerkt werd.
/// </para>
///
/// <para>
/// <b>Niets te doen is geen succes.</b> Is <c>his.Teams</c> leeg voor deze club, dan is er geen bron
/// om uit af te leiden en volgt een <c>409</c> met de reden — geen <c>200</c> met nul teams.
/// </para>
///
/// <para>
/// De Postgres-tegenhanger staat in <c>FunctionApp.Postgres/Admin/AdminTeamsHerstelFunction.cs</c>
/// en levert dezelfde route en hetzelfde antwoord.
/// </para>
/// </summary>
public static class AdminTeamsHerstelFunction
{
    [Function("AdminTeamsHerstel")]
    public static Task<IActionResult> Herstel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/teams/herstel")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminTeamsHerstel");
        return AdminEndpoint.ExecuteAsync(req, log, "canonieke teamlijst herstellen",
            async clubCode =>
            {
                var bronRijen = await TelAsync(
                    "SELECT COUNT(*) FROM [his].[Teams] WHERE [ClubCode] = @clubCode", clubCode);
                if (bronRijen == 0)
                {
                    log.LogWarning(
                        "TEAMHERSTEL - his.Teams is leeg voor club {ClubCode}; niets om uit af te leiden",
                        clubCode);
                    return new ObjectResult(new
                    {
                        error = "Er zijn geen gesynchroniseerde teams om de teamlijst uit op te bouwen. "
                                + "Draai eerst een synchronisatie."
                    })
                    { StatusCode = 409 };
                }

                var teamsVoor = await TelAsync(
                    "SELECT COUNT(*) FROM [dbo].[Teams] WHERE [ClubCode] = @clubCode AND [IsActief] = 1", clubCode);

                // ÉÉN aanroep, niet twee. RefreshAsync draait de sleutelmigratie zelf al als
                // eerste stap; een tweede, expliciete aanroep erna rapporteert altijd 0/0 en
                // kost een extra ronde over de database. Zie de Postgres-tegenhanger voor hoe
                // dit is opgemerkt (negatieve controle op poort G5b van de zelftest).
                var uitkomst = await TeamCanonicalisatieService.RefreshAsync(clubCode, log);

                var teamsNa = await TelAsync(
                    "SELECT COUNT(*) FROM [dbo].[Teams] WHERE [ClubCode] = @clubCode AND [IsActief] = 1", clubCode);
                var aliassenNa = await TelAsync(
                    "SELECT COUNT(*) FROM [dbo].[TeamAliassen] WHERE [ClubCode] = @clubCode AND [Status] = 'validated'",
                    clubCode);

                log.LogInformation(
                    "TEAMHERSTEL - club {ClubCode}: {Voor} -> {Na} actieve teams, {Aliassen} gevalideerde "
                    + "schrijfwijzen, {Sleutels} sleutels gemigreerd, {Dubbelen} dubbelen opgeruimd",
                    clubCode, teamsVoor, teamsNa, aliassenNa, uitkomst.SleutelsBijgewerkt,
                    uitkomst.DubbelenOpgeruimd);

                return new OkObjectResult(new
                {
                    clubCode,
                    teamsVoor,
                    teamsNa,
                    aliassenNa,
                    sleutelsGemigreerd = uitkomst.SleutelsBijgewerkt,
                    dubbelenOpgeruimd = uitkomst.DubbelenOpgeruimd
                });
            });
    }

    private static async Task<int> TelAsync(string sql, string clubCode)
    {
        using var conn = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
    }
}
