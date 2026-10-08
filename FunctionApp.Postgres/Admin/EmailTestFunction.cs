using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Admin;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using Npgsql;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Processing;
using FunctionApp.Postgres.TeamResolution;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/EmailTestFunction.cs</c> (#889) — vrijwel
/// woordelijke kopie. Zie <see cref="BerichtPipeline"/> voor de drie bewuste, gedocumenteerde
/// scope-afwijkingen (opponent-lookup, teamcontact-opvragen, verzet-zonder-datum).
///
/// <para>
/// Geen <c>TeamlijstGereedheid</c> op deze tier. Sinds #1568 deel C roept de dry-run
/// <c>TeamCanonicalisatieService.RefreshAsync</c> alleen nog aan als de teamlijst van de gekozen club leeg is
/// (de productieprocessor ververst hem wel elke batch): een dry-run hoort niet te schrijven.
/// </para>
///
/// POST /api/test/email
/// Body: { "onderwerp": "...", "afzender": "...", "body": "..." }
///
/// Verstuurt NIETS en slaat NIETS op (de enige schrijfactie: een lege teamlijst van de gekozen club wordt eenmalig
/// opgebouwd, zie het commentaar bij de teamresolutie). Retourneert:
///   - classificatie (AI-output)
///   - mogelijke planner-actie (puur info, geen DB-mutatie)
///   - voorbeeldantwoord (zou-worden-verstuurd via templates)
///
/// Rate limiting: max 10 calls per minuut (statisch ConcurrentQueue).
/// </summary>
public static class EmailTestFunction
{
    [Function("EmailTestDryRun")]
    public static Task<IActionResult> DryRun(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "test/email")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("EmailTestDryRun");
        // #677: de wrapper levert de clubcode uit de GUI-clubswitcher (X-Club-Code header) — zonder
        // dit gebruikt de Email-tester altijd de primaire (echte) club, ook als AllStars FC was
        // geselecteerd.
        return AdminEndpoint.ExecuteAsync(req, log, "dry-run e-mail",
            async clubCode =>
            {
                if (EmailTestEndpointCore.ControleerLimiet() is { } begrensd) return begrensd;

                // Eigen catch naast de wrapper (#1350), zie EmailTestEndpointCore.Fout.
                try
                {
                    var (dto, fout) = await EmailTestEndpointCore.LeesRequestAsync(req);
                    return dto is null ? fout! : await VoerUitAsync(dto, context, log, clubCode);
                }
                catch (Exception ex)
                {
                    return EmailTestEndpointCore.Fout(ex, log);
                }
            });
    }

    private static async Task<IActionResult> VoerUitAsync(
        TestEmailRequest dto, FunctionContext context, ILogger log, string clubCode)
    {
        var clubSettings = await LoadClubSettingsSnapshotAsync(clubCode);

        var onderwerp = dto.Onderwerp ?? "";
        var afzender = dto.Afzender ?? "trainer@voorbeeld.nl";
        var body = dto.Body ?? "";

        // #1568 deel C: dezelfde stappen als productie. De processor classificeert met de gevalideerde
        // leermomenten als few-shot voorbeelden; de tester doet dat nu ook (en meldt het aantal in de trace).
        var trace = new TraceBuilder();
        var classificatie = await ClassificeerAsync(context, clubCode, log, trace, body, onderwerp, afzender);

        var fakeEmail = new InkomendBericht
        {
            MessageId = "dry-run-" + Guid.NewGuid().ToString("N"),
            ConversationId = "",
            Afzender = afzender,
            AfzenderNaam = dto.AfzenderNaam ?? afzender.Split('@').FirstOrDefault() ?? afzender,
            Onderwerp = onderwerp,
            OntvangstDatum = DateTime.UtcNow,
            Body = body
        };

        BerichtPipeline.ValideerDagDatum(classificatie, body, onderwerp);

        // Teamresolutie ook in de dry-run (#700/#889); de democlub wordt hier net zo goed getest als de
        // primaire club. De dry-run schrijft niets (#1568 deel C): de teamlijst wordt daarom alleen
        // opgebouwd als hij voor deze club nog helemaal leeg is (bijv. een democlub die nooit gesynchroniseerd
        // is) — anders resolvet niets. Is hij gevuld, dan blijft hij onaangeroerd; de nachtelijke sync en de
        // processor houden hem actueel.
        var teamRepository = new TeamCandidateRepository(PostgresDatabaseConfig.ConnectionString);
        if (!await teamRepository.HeeftActieveTeamsAsync(clubCode))
            await TeamCanonicalisatieService.RefreshAsync(PostgresDatabaseConfig.ConnectionString, clubCode, log);

        var plannerResponseJson = await BerichtPipeline.VerwerkMetPlannerAsync(
            classificatie, fakeEmail, log, new TeamResolver(teamRepository), clubCode, clubSettings, trace);
        var (voorbeeldOnderwerp, voorbeeldBody) = await BerichtPipeline.BouwTemplateAntwoord(
            classificatie, plannerResponseJson, fakeEmail, log, clubSettings, clubCode, trace);

        // #1583: het eindoordeel weegt de ACTUELE zekerheidspoort-instelling van de gekozen club mee en volgt de
        // volgorde van de productieverwerking (reply-beleid, dan poort). Alleen lezen; de dry-run slaat niets op.
        var (poortActief, reply) = (await ZekerheidspoortInstelling.IsActiefAsync(PostgresDatabaseConfig.ConnectionString, clubCode, log), ReplyPolicy.Bepaal(classificatie, plannerResponseJson));

        return EmailTestEndpointCore.Antwoord(classificatie, classificatie.Type.ToString(), classificatie.Samenvatting,
            plannerResponseJson, trace, new TesterBeleid(poortActief, reply.MoetVersturen, reply.Reden, EmailReviewModus.IsActief()), voorbeeldOnderwerp, voorbeeldBody);
    }

    private static async Task<BerichtClassificatie> ClassificeerAsync(
        FunctionContext context, string clubCode, ILogger log, TraceBuilder trace, string body, string onderwerp, string afzender)
    {
        var chatClient = context.InstanceServices.GetService<Microsoft.Extensions.AI.IChatClient>()
            ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");
        var aiService = new BerichtAiService(
            context.InstanceServices.GetRequiredService<ILoggerFactory>().CreateLogger<BerichtAiService>(), chatClient);

        var voorbeelden = await LearningMomentRepository.HaalVoorbeeldenOpAsync(
            PostgresDatabaseConfig.ConnectionString, clubCode, log);
        trace.Leermomenten(voorbeelden.Count);
        return await aiService.ClassificeerBerichtAsync(body, onderwerp, afzender, voorbeelden.Count > 0 ? voorbeelden : null);
    }

    /// <summary>
    /// Haalt de dbo.AppSettings-rij op van de opgegeven club (#677/#889). Zelfde queryvorm als
    /// <c>AdminSettingsFunction.Get</c>, beperkt tot de velden die de auto-reply handtekening, de
    /// herplan-deadline en het "verzet zonder datum"-pad (#561/#1141) bepalen —
    /// <c>knvbpdfbijlageingeschakeld</c>/<c>knvbstandaardregio</c> bestaan onvoorwaardelijk sinds
    /// migratie 003, dus geen optionele-kolom-dans nodig.
    /// </summary>
    private static async Task<ClubAppSettingsSnapshot> LoadClubSettingsSnapshotAsync(string clubCode)
    {
        await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(@"
            SELECT plannerafzendernaam, coordinatornaam, coordinatorfunctie,
                   emailvoetnoot, herplandeadlinedagen,
                   knvbpdfbijlageingeschakeld, knvbstandaardregio
            FROM public.appsettings
            WHERE clubcode = @clubcode", connection);
        command.Parameters.AddWithValue("clubcode", clubCode);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Geen public.appsettings rij gevonden voor de opgegeven club-code");

        return new ClubAppSettingsSnapshot(
            PlannerAfzenderNaam: reader.IsDBNull(0) ? null : reader.GetString(0),
            CoordinatorNaam: reader.IsDBNull(1) ? null : reader.GetString(1),
            CoordinatorFunctie: reader.IsDBNull(2) ? null : reader.GetString(2),
            EmailVoetnoot: reader.IsDBNull(3) ? null : reader.GetString(3),
            HerplanDeadlineDagen: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            KnvbPdfBijlageIngeschakeld: reader.IsDBNull(5) ? null : reader.GetBoolean(5),
            KnvbStandaardRegio: reader.IsDBNull(6) ? null : reader.GetString(6));
    }
}
