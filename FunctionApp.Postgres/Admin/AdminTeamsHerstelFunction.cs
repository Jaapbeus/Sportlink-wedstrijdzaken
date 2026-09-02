using FunctionApp.Postgres.TeamResolution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// <c>POST /api/beheer/teams/herstel</c> — bouwt de canonieke teamlijst opnieuw op uit
/// <c>his.teams</c> (#946).
///
/// <para>
/// <b>Waarom dit bestaat.</b> <c>public.teams</c>/<c>public.teamaliassen</c> zijn afgeleide tabellen;
/// ze worden gevuld aan het eind van elke synchronisatie. Staat die uit, of heeft die sinds de
/// uitrol nog niet gelopen, dan kon een beheerder niets doen behalve wachten: de teamkeuzelijst
/// bleef leeg, <c>team-schedule</c> gaf 404, en sinds #945 meldt de beschikbaarheidscontrole eerlijk
/// dat zij niets kón controleren — maar dat maakt het nog niet herstelbaar. Dit endpoint is die
/// hendel.
/// </para>
///
/// <para>
/// <b>Waarom een expliciete POST en niet automatisch bij een leesverzoek.</b> Bij de analyse van
/// #931 is dat alternatief gewogen en afgewezen: het zou <c>GET</c>-paden schrijvend maken op een
/// database met automatische pauzering en een verbruiksbudget, op een platform dat kan uitschalen
/// naar meerdere instanties. Herstel hoort achter een handeling van een mens.
/// </para>
///
/// <para>
/// <b>Onvoorwaardelijk, anders dan <c>TeamlijstGereedheid</c> op de SQL Server-tier.</b> Die klasse
/// vult alleen aan wanneer de lijst leeg is. Hier heeft iemand expliciet om herstel gevraagd, dus
/// draaien altijd beide stappen: de volledige canonicalisatie én de sleutelmigratie (#766), die
/// nodig is wanneer de opgeslagen sleutels nog volgens oudere normalisatieregels berekend zijn.
/// </para>
///
/// <para>
/// <b>Niets te doen is geen succes.</b> Is <c>his.teams</c> leeg voor deze club, dan is er geen bron
/// om uit af te leiden en volgt een <c>409</c> met de reden — geen <c>200</c> met nul teams. Anders
/// zou "de synchronisatie heeft nog nooit gelopen" niet te onderscheiden zijn van "hersteld".
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
                var cs = PostgresDatabaseConfig.ConnectionString;

                var bronRijen = await TelAsync(cs,
                    "SELECT COUNT(*) FROM his.teams WHERE clubcode = @clubcode", clubCode);
                if (bronRijen == 0)
                {
                    log.LogWarning(
                        "TEAMHERSTEL - his.teams is leeg voor club {ClubCode}; niets om uit af te leiden",
                        clubCode);
                    return new ObjectResult(new
                    {
                        error = "Er zijn geen gesynchroniseerde teams om de teamlijst uit op te bouwen. "
                                + "Draai eerst een synchronisatie."
                    })
                    { StatusCode = 409 };
                }

                var teamsVoor = await TelAsync(cs,
                    "SELECT COUNT(*) FROM public.teams WHERE clubcode = @clubcode AND isactief = TRUE", clubCode);

                // ÉÉN aanroep, niet twee. RefreshAsync draait de sleutelmigratie zelf al als
                // eerste stap; een tweede, expliciete aanroep erna rapporteert altijd 0/0 en
                // kost een extra ronde over de database. Dat stond hier eerst wél, en het is
                // opgevangen door de negatieve controle op poort G5b van de zelftest: met die
                // tweede aanroep uitgeschakeld bleef de poort groen — het bewijs dat hij niets
                // toevoegde.
                var uitkomst = await TeamCanonicalisatieService.RefreshAsync(cs, clubCode, log);

                var teamsNa = await TelAsync(cs,
                    "SELECT COUNT(*) FROM public.teams WHERE clubcode = @clubcode AND isactief = TRUE", clubCode);
                var aliassenNa = await TelAsync(cs,
                    "SELECT COUNT(*) FROM public.teamaliassen WHERE clubcode = @clubcode AND status = 'validated'",
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

    private static async Task<int> TelAsync(string connectionString, string sql, string clubCode)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
    }
}
