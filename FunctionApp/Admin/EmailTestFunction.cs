using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
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
/// Verstuurt NIETS en slaat NIETS op. Retourneert:
///   - classificatie (AI-output)
///   - mogelijke planner-actie (puur info, geen DB-mutatie)
///   - voorbeeldantwoord (zou-worden-verstuurd via templates)
///
/// Rate limiting: max 10 calls per minuut (statisch ConcurrentQueue).
/// </summary>
public static class EmailTestFunction
{
    private const int MaxCallsPerMinute = 10;
    private static readonly ConcurrentQueue<DateTime> _calls = new();
    private static readonly object _lock = new();

    [Function("EmailTestDryRun")]
    public static async Task<IActionResult> DryRun(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "test/email")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("EmailTestDryRun");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });

        // #677: respecteer de GUI-clubswitcher (X-Club-Code header) — zonder dit gebruikte de
        // Email-tester altijd de primaire (echte) club, ook als AllStars FC was geselecteerd.
        var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);

        if (!TryAcquireSlot())
        {
            return new ObjectResult(new { error = $"Rate limit overschreden: max {MaxCallsPerMinute}/min" })
            {
                StatusCode = 429
            };
        }

        try
        {
            using var bodyReader = new StreamReader(req.Body);
            var bodyText = await bodyReader.ReadToEndAsync();
            var dto = JsonConvert.DeserializeObject<TestEmailRequest>(bodyText);
            if (dto == null || string.IsNullOrWhiteSpace(dto.Body))
                return new BadRequestObjectResult(new { error = "Onderwerp/afzender/body verplicht" });

            await SystemUtilities.WaitForDatabaseAsync(log);
            await SystemUtilities.AppSettings.LoadSettingsAsync(log);

            // #677: club-specifieke settings-snapshot i.p.v. de proces-globale cache, zodat een
            // AllStars-dry-run nooit de instellingen (afzendernaam/coördinator) van de echte
            // productieclub gebruikt.
            var clubSettings = await LoadClubSettingsSnapshotAsync(clubCode);

            var loggerFactory = context.InstanceServices.GetRequiredService<ILoggerFactory>();
            var chatClient = context.InstanceServices.GetService<Microsoft.Extensions.AI.IChatClient>()
                ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");
            var aiService = new BerichtAiService(loggerFactory.CreateLogger<BerichtAiService>(), chatClient);

            var onderwerp = dto.Onderwerp ?? "";
            var afzender = dto.Afzender ?? "trainer@voorbeeld.nl";
            var body = dto.Body ?? "";

            var classificatie = await aiService.ClassificeerBerichtAsync(body, onderwerp, afzender);

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

            // De teamlijst van de geselecteerde club moet bruikbaar zijn vóór de resolutie (#766).
            // De echte pipeline doet dit al voor de primaire club; de tester werkt óók met de
            // democlub, en juist die lijst wordt door geen enkel ander pad gecontroleerd.
            var gereedheid = context.InstanceServices.GetService<TeamlijstGereedheid>();
            if (gereedheid != null)
                await gereedheid.ZorgVoorTeamlijstAsync(clubCode);

            var plannerResponseJson = await BerichtPipeline.VerwerkMetPlannerAsync(
                classificatie, fakeEmail, log, teamResolver, clubCode, clubSettings);
            // clubCode expliciet meegeven: zonder dat leest EmailTemplateService de templates van de
            // primaire club, terwijl de tester de club uit de GUI-clubswitcher toont (#677/#706).
            var (voorbeeldOnderwerp, voorbeeldBody) = await BerichtPipeline.BouwTemplateAntwoord(
                classificatie, plannerResponseJson, fakeEmail, log, clubSettings, clubCode);

            return new OkObjectResult(new
            {
                dryRun = true,
                opmerking = "Dit verstuurt niets en slaat niets op",
                classificatie,
                plannerResponse = System.Text.Json.JsonDocument.Parse(plannerResponseJson).RootElement,
                voorbeeldAntwoord = new
                {
                    onderwerp = voorbeeldOnderwerp,
                    body = voorbeeldBody
                }
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij dry-run email");
            var isLocal = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
            var errorMsg = isLocal ? $"Dry-run mislukt: {ex.GetType().Name}: {ex.Message}" : "Dry-run mislukt";
            return new ObjectResult(new { error = errorMsg }) { StatusCode = 500 };
        }
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

    private static bool TryAcquireSlot()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            while (_calls.TryPeek(out var first) && first < cutoff)
            {
                _calls.TryDequeue(out _);
            }
            if (_calls.Count >= MaxCallsPerMinute) return false;
            _calls.Enqueue(DateTime.UtcNow);
            return true;
        }
    }

    public class TestEmailRequest
    {
        public string? Onderwerp { get; set; }
        public string? Afzender { get; set; }
        public string? AfzenderNaam { get; set; }
        public string? Body { get; set; }
    }
}
