using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Admin;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using SportlinkFunction.Email;
using SportlinkFunction.Processing;
using SportlinkFunction.TeamResolution;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor dry-run email classificatie. v2 — #92.
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
        // dit gebruikte de Email-tester altijd de primaire (echte) club, ook als AllStars FC was
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
        await SystemUtilities.AppSettings.LoadSettingsAsync(log);

        // #677: club-specifieke settings-snapshot i.p.v. de proces-globale cache, zodat een
        // AllStars-dry-run nooit de instellingen (afzendernaam/coördinator) van de echte
        // productieclub gebruikt.
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

        // Teamresolutie ook in de dry-run, zodat de tester exact hetzelfde gedrag laat zien als de
        // echte verwerking (#700). Verplicht: zonder resolver wordt er geen team meer herkend.
        var teamResolver = context.InstanceServices.GetRequiredService<ITeamResolver>();

        // De teamlijst van de geselecteerde club moet bruikbaar zijn vóór de resolutie (#766). De tester
        // werkt óók met de democlub, en juist die lijst wordt door geen enkel ander pad gecontroleerd.
        // De dry-run schrijft niets (#1568 deel C): TeamlijstGereedheid migreert ook bij een gevulde lijst
        // sleutels, dus hij wordt alleen aangeroepen als de lijst nog helemaal leeg is. Is hij gevuld, dan
        // blijft hij onaangeroerd; de nachtelijke sync en de processor houden hem actueel.
        var gereedheid = context.InstanceServices.GetService<TeamlijstGereedheid>();
        var teamRepository = context.InstanceServices.GetService<ITeamCandidateRepository>();
        if (gereedheid != null && teamRepository != null && !await teamRepository.HeeftActieveTeamsAsync(clubCode))
            await gereedheid.ZorgVoorTeamlijstAsync(clubCode);

        var plannerResponseJson = await BerichtPipeline.VerwerkMetPlannerAsync(
            classificatie, fakeEmail, log, teamResolver, clubCode, clubSettings, trace);
        // clubCode expliciet meegeven: zonder dat leest EmailTemplateService de templates van de
        // primaire club, terwijl de tester de club uit de GUI-clubswitcher toont (#677/#706).
        var (voorbeeldOnderwerp, voorbeeldBody) = await BerichtPipeline.BouwTemplateAntwoord(
            classificatie, plannerResponseJson, fakeEmail, log, clubSettings, clubCode, trace);

        // #1583: het eindoordeel weegt de ACTUELE zekerheidspoort-instelling van de gekozen club mee en volgt de
        // volgorde van de productieverwerking (reply-beleid, dan poort). Alleen lezen; de dry-run slaat niets op.
        var (poortActief, reply) = (await ZekerheidspoortInstelling.IsActiefAsync(clubCode, log), ReplyPolicy.Bepaal(classificatie, plannerResponseJson));

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

        var voorbeelden = await LearningMomentRepository.HaalVoorbeeldenOpAsync(clubCode, log);
        trace.Leermomenten(voorbeelden.Count);
        return await aiService.ClassificeerBerichtAsync(body, onderwerp, afzender, voorbeelden.Count > 0 ? voorbeelden : null);
    }

    /// <summary>
    /// Haalt de dbo.AppSettings-rij op van de opgegeven club (#677). Zelfde queryvorm als
    /// AdminSettingsFunction.Get, maar beperkt tot de velden die de auto-reply handtekening en de
    /// herplan-deadline bepalen. Gebruikt om de Email-tester club-bewust te maken: de proces-globale
    /// SystemUtilities.AppSettings cache bevat altijd de primaire (echte) club, nooit AllStars FC.
    /// </summary>
    private static async Task<ClubAppSettingsSnapshot> LoadClubSettingsSnapshotAsync(string clubCode)
    {
        using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();

        // #561: KnvbPdfBijlageIngeschakeld/KnvbStandaardRegio bestaan pas na migratie — dynamisch
        // detecteren zodat de Email-tester ook werkt tegen een database die nog niet gemigreerd is
        // (zelfde patroon als UseRealtimeApi/SyncEnabled in SystemUtilities.AppSettings).
        using var colCheckCommand = new SqlCommand(@"
            SELECT
                COL_LENGTH('[dbo].[AppSettings]', 'KnvbPdfBijlageIngeschakeld'),
                COL_LENGTH('[dbo].[AppSettings]', 'KnvbStandaardRegio')", connection);
        using var colCheckReader = await colCheckCommand.ExecuteReaderAsync();
        var heeftKnvbKolommen = false;
        if (await colCheckReader.ReadAsync())
            heeftKnvbKolommen = !colCheckReader.IsDBNull(0) && !colCheckReader.IsDBNull(1);
        await colCheckReader.DisposeAsync();

        var knvbSelect = heeftKnvbKolommen
            ? ", [KnvbPdfBijlageIngeschakeld], [KnvbStandaardRegio]"
            : "";

        using var command = new SqlCommand($@"
            SELECT TOP 1 [PlannerAfzenderNaam], [CoordinatorNaam], [CoordinatorFunctie],
                   [EmailVoetnoot], [HerplanDeadlineDagen]{knvbSelect}
            FROM [dbo].[AppSettings]
            WHERE [ClubCode] = @ClubCode", connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);

        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Geen dbo.AppSettings rij gevonden voor de opgegeven club-code");

        return new ClubAppSettingsSnapshot(
            PlannerAfzenderNaam: reader.IsDBNull(0) ? null : reader.GetString(0),
            CoordinatorNaam: reader.IsDBNull(1) ? null : reader.GetString(1),
            CoordinatorFunctie: reader.IsDBNull(2) ? null : reader.GetString(2),
            EmailVoetnoot: reader.IsDBNull(3) ? null : reader.GetString(3),
            HerplanDeadlineDagen: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            KnvbPdfBijlageIngeschakeld: heeftKnvbKolommen && !reader.IsDBNull(5) ? reader.GetBoolean(5) : null,
            KnvbStandaardRegio: heeftKnvbKolommen && !reader.IsDBNull(6) ? reader.GetString(6) : null);
    }
}
