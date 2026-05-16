using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SportlinkFunction.Email;

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
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "test/email")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("EmailTestDryRun");

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

            var loggerFactory = context.InstanceServices.GetRequiredService<ILoggerFactory>();
            var aiService = new EmailAiService(loggerFactory.CreateLogger<EmailAiService>());

            var onderwerp = dto.Onderwerp ?? "";
            var afzender = dto.Afzender ?? "test@example.com";
            var body = dto.Body ?? "";

            var classificatie = await aiService.ClassificeerEmailAsync(body, onderwerp, afzender);

            var fakeEmail = new InkomendEmail
            {
                MessageId = "dry-run-" + Guid.NewGuid().ToString("N"),
                ConversationId = "",
                Afzender = afzender,
                AfzenderNaam = afzender.Split('@').FirstOrDefault() ?? afzender,
                Onderwerp = onderwerp,
                OntvangstDatum = DateTime.UtcNow,
                Body = body
            };

            EmailProcessorFunction.ValideerDagDatum(classificatie, body, onderwerp);
            var plannerResponseJson = await EmailProcessorFunction.VerwerkMetPlannerAsync(classificatie, fakeEmail, log);
            var (voorbeeldOnderwerp, voorbeeldBody) = EmailProcessorFunction.BouwTemplateAntwoord(classificatie, plannerResponseJson, fakeEmail);

            return new OkObjectResult(new
            {
                dryRun = true,
                opmerking = "Dit verstuurt niets en slaat niets op",
                classificatie,
                plannerResponse = Newtonsoft.Json.Linq.JToken.Parse(plannerResponseJson),
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
            return new ObjectResult(new { error = "Dry-run mislukt" }) { StatusCode = 500 };
        }
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
        public string? Body { get; set; }
    }
}
