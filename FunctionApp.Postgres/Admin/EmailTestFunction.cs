using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
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
/// Geen <c>TeamlijstGereedheid</c> op deze tier: <c>AdminTeamsHerstelFunction</c> roept
/// <c>TeamCanonicalisatieService.RefreshAsync</c> hier onvoorwaardelijk aan (zie die klasse se
/// eigen documentatie), dus deze functie doet hetzelfde vóór teamresolutie — idempotent, "kan
/// zonder bezwaar herhaald worden".
/// </para>
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

        // #677: respecteer de GUI-clubswitcher (X-Club-Code header) — zonder dit gebruikt de
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

            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            await PostgresAppSettings.LoadSettingsAsync(log);

            var clubSettings = await LoadClubSettingsSnapshotAsync(clubCode);

            var chatClient = context.InstanceServices.GetService<Microsoft.Extensions.AI.IChatClient>()
                ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");
            var aiService = new BerichtAiService(
                context.InstanceServices.GetRequiredService<ILoggerFactory>().CreateLogger<BerichtAiService>(),
                chatClient);

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

            // Teamresolutie ook in de dry-run (#700/#889): eerst de canonieke teamlijst verzekeren
            // (zelfde herstelpad als AdminTeamsHerstelFunction, idempotent), dan pas resolven — de
            // democlub wordt hier net zo goed getest als de primaire club.
            await TeamCanonicalisatieService.RefreshAsync(PostgresDatabaseConfig.ConnectionString, clubCode, log);
            var teamResolver = new TeamResolver(new TeamCandidateRepository(PostgresDatabaseConfig.ConnectionString));

            var plannerResponseJson = await BerichtPipeline.VerwerkMetPlannerAsync(
                classificatie, fakeEmail, log, teamResolver, clubCode, clubSettings);
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
    /// Haalt de dbo.AppSettings-rij op van de opgegeven club (#677/#889). Zelfde queryvorm als
    /// <c>AdminSettingsFunction.Get</c>, beperkt tot de velden die de auto-reply handtekening en de
    /// herplan-deadline bepalen. <c>KnvbPdfBijlageIngeschakeld</c>/<c>KnvbStandaardRegio</c> staan
    /// hier bewust NIET bij (in tegenstelling tot het SQL Server-origineel): die kolommen bestaan
    /// niet in <c>public.appsettings</c> op deze tier, en het "verzet zonder datum"-pad dat ze nodig
    /// heeft is hier niet vertaald (zie <see cref="BerichtPipeline"/>).
    /// </summary>
    private static async Task<ClubAppSettingsSnapshot> LoadClubSettingsSnapshotAsync(string clubCode)
    {
        await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(@"
            SELECT plannerafzendernaam, coordinatornaam, coordinatorfunctie,
                   emailvoetnoot, herplandeadlinedagen
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
            HerplanDeadlineDagen: reader.IsDBNull(4) ? null : reader.GetInt32(4));
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
