using System.Text.Json;
using Microsoft.Playwright;

namespace SportlinkTokenCapture;

internal static class Program
{
    private const string TokenEndpointFragment = "/protocol/openid-connect/token";
    private const string LoginUrl = "https://club.sportlink.com/dashboard";

    // Elke rol (bv. "Wedstrijdzaken", "Secretariaat") logt in met een eigen, los Sportlink-account
    // met eigen, smal-geschaalde rechten (via Sportlink's /club-maintenance/users-roles) — dus
    // een eigen refresh_token, opgeslagen onder een eigen instellingennaam. Zo blijft elke rol in
    // Sportlink's eigen audit-log herkenbaar als "webapp-<rol>", niet als een persoonsnaam, en
    // blijft een rol met beperkte Sportlink-rechten ook echt beperkt als onze eigen rolcheck ooit
    // een gat heeft (tweede verdedigingslinie, niet alleen UI-niveau).
    private static string SettingsKeyFor(string role) => $"SportlinkClubRefreshToken__{role}";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.WriteLine("Gebruik: dotnet run --project Tools/SportlinkTokenCapture -- <rol>");
            Console.WriteLine("Voorbeeld: dotnet run --project Tools/SportlinkTokenCapture -- Wedstrijdzaken");
            Console.WriteLine("Log in het geopende venster in met het Sportlink-account dat bij deze rol hoort.");
            return 1;
        }
        var role = args[0];
        var settingsKey = SettingsKeyFor(role);

        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Console.WriteLine("Kon de repository-root niet vinden (geen FunctionApp.Postgres-map in de bovenliggende mappen). Draai dit tool vanuit de repo.");
            return 1;
        }

        var settingsPath = Path.Combine(repoRoot, "FunctionApp.Postgres", "local.settings.json");
        if (!File.Exists(settingsPath))
        {
            Console.WriteLine($"Bestand niet gevonden: {settingsPath}");
            Console.WriteLine("Kopieer eerst FunctionApp.Postgres/local.settings.template.json naar local.settings.json.");
            return 1;
        }

        using var playwright = await Playwright.CreateAsync();
        IBrowser browser;
        try
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist"))
        {
            Console.WriteLine("Playwright-browserbinaries ontbreken nog. Draai eenmalig:");
            Console.WriteLine("  dotnet build Tools/SportlinkTokenCapture/SportlinkTokenCapture.csproj");
            Console.WriteLine("  pwsh Tools/SportlinkTokenCapture/bin/Debug/net9.0/playwright.ps1 install chromium");
            return 1;
        }

        await using var browserDisposable = browser;
        var page = await browser.NewPageAsync();

        string? capturedRefreshToken = null;
        int? expiresIn = null;
        int? refreshExpiresIn = null;

        // We vangen het token op uit de NETWERK-respons van de token-uitwisseling zelf, niet uit
        // localStorage: Sportlink versleutelt alles wat het daar zet (zie
        // docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §2.6) — de token-respons zelf is het
        // enige moment dat de waarde onversleuteld bestaat.
        page.Response += async (_, response) =>
        {
            if (capturedRefreshToken is not null) return;
            if (!response.Url.Contains(TokenEndpointFragment)) return;
            if (response.Request.Method != "POST") return;

            try
            {
                var body = await response.TextAsync();
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("refresh_token", out var refreshTokenElement)) return;

                capturedRefreshToken = refreshTokenElement.GetString();
                expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : null;
                refreshExpiresIn = doc.RootElement.TryGetProperty("refresh_expires_in", out var r) ? r.GetInt32() : null;
                Console.WriteLine($"Token-respons opgevangen (expires_in={expiresIn}s, refresh_expires_in={refreshExpiresIn}s). Waarde wordt niet getoond.");
            }
            catch (JsonException)
            {
                // Niet elke response op dit pad is per se de echte grant-respons (bv. een
                // afgewezen poging zonder refresh_token) — negeren en wachten op de volgende.
            }
        };

        Console.WriteLine($"Rol: {role} — log in met het Sportlink-account dat bij deze rol hoort (inclusief eventuele MFA).");
        Console.WriteLine("Dit venster sluit zichzelf automatisch zodra het token is opgevangen (max. 5 minuten).");
        await page.GotoAsync(LoginUrl);

        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (capturedRefreshToken is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
        }

        await browser.CloseAsync();

        if (capturedRefreshToken is null)
        {
            Console.WriteLine("Timeout: geen token-respons opgevangen binnen 5 minuten. Niets opgeslagen.");
            return 1;
        }

        WriteRefreshTokenToSettings(settingsPath, settingsKey, capturedRefreshToken);
        Console.WriteLine($"Refresh-token opgeslagen als '{settingsKey}' in {settingsPath}. Waarde niet getoond.");
        return 0;
    }

    private static void WriteRefreshTokenToSettings(string settingsPath, string settingsKey, string refreshToken)
    {
        var json = File.ReadAllText(settingsPath);
        using var doc = JsonDocument.Parse(json);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name == "Values")
                {
                    writer.WritePropertyName("Values");
                    writer.WriteStartObject();
                    foreach (var valueProperty in property.Value.EnumerateObject())
                    {
                        if (valueProperty.Name == settingsKey) continue;
                        valueProperty.WriteTo(writer);
                    }
                    writer.WriteString(settingsKey, refreshToken);
                    writer.WriteEndObject();
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        File.WriteAllBytes(settingsPath, stream.ToArray());
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "FunctionApp.Postgres")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
