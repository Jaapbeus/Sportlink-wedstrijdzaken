using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Planner.Shared;
using Planner.Shared.Infrastructure;

namespace Planner.Endpoints.Teambegeleiding;

/// <summary>
/// De gedeelde toegangs- en gebruiksgrens van <c>POST /api/beheer/teambegeleiding/doorsturen</c> op
/// beide tiers (#1671). Een meegegeven ontvangersveld is voor de rol <c>admin</c> vrij; voor een
/// niet-beheerder moet elk adres bij het gekozen team horen (begeleiders van dat team, de
/// coördinator of de aanvrager zelf). Het aantal daadwerkelijke verzendingen van een niet-beheerder
/// is begrensd; afgewezen of mislukte pogingen tellen niet mee.
/// </summary>
public static class TeambegeleidingDoorsturenEndpointCore
{
    public const int MaxPerVenster = 3;
    public static readonly TimeSpan Venster = TimeSpan.FromMinutes(10);

    private static readonly GebruikerLimiet Limiet = new(MaxPerVenster, Venster);

    /// <summary>
    /// De volgorde van het doorsturen, gedeeld door beide tiers: ontvangers bepalen (400/403/503),
    /// e-mailservice beschikbaar (503), dan pas een limietslot (429) en de verzending. Zo verbruikt
    /// alleen een poging die daadwerkelijk verstuurd wordt een slot; mislukt de verzending, dan gaat
    /// het slot terug en wordt de fout doorgegeven.
    /// </summary>
    public static async Task<IActionResult> VoerUitAsync(
        Aanvrager aanvrager,
        Func<Task<(List<string>? Ontvangers, IActionResult? Fout)>> bepaalOntvangers,
        bool emailServiceBeschikbaar,
        Func<List<string>, Task> verstuur,
        string teamNaam,
        ILogger log,
        GebruikerLimiet? limiet = null)
    {
        var (ontvangers, fout) = await bepaalOntvangers();
        if (fout != null) return fout;

        if (!emailServiceBeschikbaar)
        {
            log.LogWarning("Graph SDK niet geconfigureerd — e-mail doorsturen niet mogelijk");
            return new ObjectResult(new { error = "E-mail service niet geconfigureerd" }) { StatusCode = 503 };
        }

        var gebruikteLimiet = limiet ?? Limiet;
        if (NeemVerzendslot(aanvrager.IsAdmin, aanvrager.ObjectId, gebruikteLimiet, out var slot) is { } limietFout) return limietFout;

        try
        {
            await verstuur(ontvangers!);
        }
        catch
        {
            // Een mislukte verzending telt niet mee: precies het eigen slot gaat terug.
            if (slot is { } genomen) gebruikteLimiet.TerugGeven(genomen);
            throw;
        }
        return new OkObjectResult(new
        {
            success = true,
            bericht = $"Uw vraag over de begeleiding van {teamNaam} is doorgestuurd. De begeleider neemt rechtstreeks contact met u op."
        });
    }

    /// <summary>
    /// Bepaalt de ontvangers. Ingevuld veld: zie <see cref="BeoordeelOntvangersveldAsync"/>, met als
    /// toegestane set de begeleiders van het team, de coördinator en de aanvrager zelf. Leeg veld:
    /// de voorkeursbegeleider van het team, anders de coördinator, anders 503.
    /// </summary>
    public static async Task<(List<string>? Ontvangers, IActionResult? Fout)> BepaalOntvangersAsync(
        string? ruweOntvangers, Aanvrager aanvrager, string? coordinatorEmail, OntvangerBronnen bronnen, ILogger log)
    {
        if (!string.IsNullOrWhiteSpace(ruweOntvangers))
            return await BeoordeelOntvangersveldAsync(
                ruweOntvangers, aanvrager.IsAdmin,
                async () => [.. await bronnen.BegeleiderAdressen(), coordinatorEmail, aanvrager.Email],
                bronnen.UitgeslotenAdressen);

        var begeleider = await bronnen.VoorkeursBegeleider();
        if (!string.IsNullOrEmpty(begeleider))
            return ([begeleider], null);
        if (string.IsNullOrEmpty(coordinatorEmail))
            return (null, new ObjectResult(new { error = "Geen begeleider en geen coördinator geconfigureerd" }) { StatusCode = 503 });

        log.LogWarning("Geen begeleider-e-mail gevonden voor team — doorgestuurd naar coördinator");
        return ([coordinatorEmail], null);
    }

    /// <summary>
    /// Beoordeelt een ingevuld ontvangersveld: ongeldige regel ⇒ 400; voor een niet-beheerder een
    /// adres buiten <paramref name="teamAdressen"/> ⇒ 403; adres op de uitsluitingslijst ⇒ 400.
    /// De teamadressen worden alleen opgehaald als de rolgrens ze nodig heeft.
    /// </summary>
    public static async Task<(List<string>? Ontvangers, IActionResult? Fout)> BeoordeelOntvangersveldAsync(
        string ruweOntvangers,
        bool isAdmin,
        Func<Task<IEnumerable<string?>>> teamAdressen,
        Func<Task<HashSet<string>>> uitgeslotenAdressen)
    {
        var parseResultaat = OntvangerParser.Parse(ruweOntvangers);
        if (!parseResultaat.IsValid)
            return (null, new BadRequestObjectResult(new { error = parseResultaat.FoutMelding }));

        if (!isAdmin && !HorenBijTeam(parseResultaat.Emailadressen, await teamAdressen()))
            return (null, new ObjectResult(new { error = "Ontvangers moeten bij het gekozen team horen." }) { StatusCode = 403 });

        var uitgesloten = await uitgeslotenAdressen();
        var geweigerdAdres = parseResultaat.Emailadressen.FirstOrDefault(uitgesloten.Contains);
        if (geweigerdAdres != null)
            return (null, new BadRequestObjectResult(new
            {
                error = $"E-mailadres \"{geweigerdAdres}\" staat op de uitsluitingslijst en kan niet als ontvanger worden gebruikt."
            }));

        return ([.. parseResultaat.Emailadressen], null);
    }

    /// <summary>
    /// <c>true</c> als elk adres (getrimd, hoofdletterongevoelig) in de toegestane set zit. Een
    /// toegestane waarde mag zelf een ontvangersregel zijn (bijv. een instelling met meerdere
    /// adressen of de vorm <c>"Naam" &lt;adres&gt;</c>).
    /// </summary>
    public static bool HorenBijTeam(IEnumerable<string> adressen, IEnumerable<string?> toegestaan)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var waarde in toegestaan.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w!.Trim()))
        {
            var geparsed = OntvangerParser.Parse(waarde);
            if (geparsed.IsValid) set.UnionWith(geparsed.Emailadressen);
            else set.Add(waarde);
        }
        return adressen.All(a => set.Contains(a.Trim()));
    }

    /// <summary>
    /// Neemt direct vóór de verzending een slot uit de limiet: 429 als een niet-beheerder de limiet
    /// heeft bereikt, anders <c>null</c> met het genomen slot in <paramref name="slot"/>. Beheerders
    /// vallen buiten de limiet (geen slot).
    /// </summary>
    public static IActionResult? NeemVerzendslot(bool isAdmin, string? limietSleutel, GebruikerLimiet limiet, out GebruikerLimiet.Slot? slot)
    {
        slot = null;
        if (isAdmin) return null;
        if (limiet.TryAcquire(limietSleutel, out var genomen))
        {
            slot = genomen;
            return null;
        }
        return new ObjectResult(new { error = "Er zijn net veel vragen doorgestuurd. Probeer het over enkele minuten nog eens." }) { StatusCode = 429 };
    }
}

/// <summary>De aanroeper volgens de Easy Auth-claims: rol, eigen e-mailadres en object-ID (limietsleutel).</summary>
public readonly record struct Aanvrager(bool IsAdmin, string? Email, string? ObjectId);

/// <summary>
/// De tier-specifieke query's voor het bepalen van de ontvangers (AVG: de uitkomst komt nooit in een
/// respons of logregel).
/// </summary>
/// <param name="BegeleiderAdressen">Alle e-mailadressen van de begeleiders van het gekozen team.</param>
/// <param name="VoorkeursBegeleider">Het e-mailadres van de voorkeursbegeleider (trainer vóór coach vóór teamleider).</param>
/// <param name="UitgeslotenAdressen">De actieve uitsluitingslijst van de club.</param>
public sealed record OntvangerBronnen(
    Func<Task<IEnumerable<string>>> BegeleiderAdressen,
    Func<Task<string?>> VoorkeursBegeleider,
    Func<Task<HashSet<string>>> UitgeslotenAdressen);
