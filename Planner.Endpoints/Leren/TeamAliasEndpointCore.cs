using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared;
using Planner.Shared.Email.Trace;
using Planner.Shared.Leren;

namespace Planner.Endpoints.Leren;

/// <summary>Body van <c>POST /api/beheer/teamaliassen</c>. De aanmaker staat er bewust niet in: dat komt uit het principal.</summary>
public sealed class TeamAliasAanmaakRequest
{
    public string? RuweTekst { get; set; }
    public int TeamId { get; set; }
    /// <summary>Alleen <c>true</c> als de beheerder expliciet een bestaande alias naar dit team wil herkoppelen.</summary>
    public bool Herkoppel { get; set; }
    public int? HerkomstVerwerkingId { get; set; }
    public string? Reden { get; set; }
}

/// <summary>
/// Tier-onafhankelijke orkestratie van het beheer van teamaliassen (#1568 deel C): aanmaken door een
/// beheerder (bron <c>CoordinatorCorrectie</c>, direct <c>validated</c>), valideren/afwijzen en
/// verwijderen met vastlegging van wie het deed. De databasevraag blijft per tier (<see cref="ITeamAliasStore"/>).
/// </summary>
public static class TeamAliasEndpointCore
{
    public const string BronCoordinatorCorrectie = "CoordinatorCorrectie";
    public const int MaxRuweTekstLengte = 200;
    public const int MaxRedenLengte = 200;

    public const int StandaardLimit = 100;
    public const int MaxLimit = 500;
    private static readonly string[] GeldigeStatussen = ["pending", "validated", "rejected"];

    /// <summary>Leest <c>status</c> en <c>limit</c> van de lijstaanroep; een ongeldige status geeft een 400 in <c>Fout</c>.</summary>
    public static (string Status, int Limit, IActionResult? Fout) LeesLijstFilter(IQueryCollection query)
    {
        var status = query["status"].ToString();
        var limit = int.TryParse(query["limit"].ToString(), out var l) ? Math.Min(MaxLimit, Math.Max(1, l)) : StandaardLimit;
        var fout = !string.IsNullOrWhiteSpace(status) && !GeldigeStatussen.Contains(status)
            ? new BadRequestObjectResult(new { error = "Ongeldige status. Gebruik 'pending', 'validated' of 'rejected'." })
            : null;
        return (status, limit, fout);
    }

    public static async Task<IActionResult> AanmakenAsync(
        string clubCode, string? body, LerenAanroeper wie,
        ITeamAliasStore store, IOnbekendeTeamTekstStore wachtrij, ILogger log)
    {
        var dto = LeesBody<TeamAliasAanmaakRequest>(body);
        var ruw = (dto?.RuweTekst ?? "").Trim();
        if (dto is null || ruw.Length == 0 || dto.TeamId <= 0)
            return new BadRequestObjectResult(new { error = "Teamtekst en team zijn verplicht." });
        if (ruw.Length > MaxRuweTekstLengte)
            return new BadRequestObjectResult(new { error = $"Teamtekst mag maximaal {MaxRuweTekstLengte} tekens zijn." });

        // De ene plek voor de sleutel: dezelfde functie als de resolver, geen eigen regex (#692).
        var genormaliseerd = TeamNaamNormalisatie.NormaliseerVoorVergelijking(ruw, clubCode);
        if (genormaliseerd.Length == 0)
            return new BadRequestObjectResult(new { error = "Deze tekst bevat geen herkenbare teamaanduiding." });

        var reden = string.IsNullOrWhiteSpace(dto.Reden) ? null : TraceBuilder.Saneer(dto.Reden, MaxRedenLengte);
        var uitkomst = await store.MaakAanAsync(new AliasAanmaakOpdracht(
            clubCode, ruw, genormaliseerd, dto.TeamId, dto.Herkoppel, wie, dto.HerkomstVerwerkingId, reden));

        switch (uitkomst.Status)
        {
            case AliasAanmaakStatus.TeamOnbekend:
                return new NotFoundObjectResult(new { error = $"Team {dto.TeamId} niet gevonden." });
            case AliasAanmaakStatus.Conflict:
                return new ConflictObjectResult(new
                {
                    error = $"Deze schrijfwijze hoort al bij team {uitkomst.BestaandTeamnaam ?? "onbekend"}. " +
                            "Kies bewust 'herkoppelen' om hem naar het nieuwe team te verplaatsen.",
                    bestaandeAliasId = uitkomst.Id,
                    bestaandTeamId = uitkomst.BestaandTeamId,
                    bestaandTeamnaam = uitkomst.BestaandTeamnaam,
                    bestaandeStatus = uitkomst.BestaandeStatus
                });
        }

        await RondWachtrijAfAsync(clubCode, genormaliseerd, wachtrij, log);
        var status = uitkomst.Status switch
        {
            AliasAanmaakStatus.Aangemaakt => "aangemaakt",
            AliasAanmaakStatus.Herkoppeld => "herkoppeld",
            _ => "bestaat-al"
        };
        var antwoord = new
        {
            id = uitkomst.Id, status, ruweTekst = ruw, ruweTekstGenormaliseerd = genormaliseerd,
            teamId = dto.TeamId, teamnaam = uitkomst.Teamnaam
        };
        return uitkomst.Status == AliasAanmaakStatus.Aangemaakt
            ? new ObjectResult(antwoord) { StatusCode = 201 }
            : new OkObjectResult(antwoord);
    }

    /// <summary>Een alias hoort een open wachtrijregel met dezelfde sleutel af te handelen; een storing daar mag de alias niet ongedaan maken.</summary>
    private static async Task RondWachtrijAfAsync(string clubCode, string genormaliseerd, IOnbekendeTeamTekstStore wachtrij, ILogger log)
    {
        try { await wachtrij.MarkeerAfgehandeldAsync(clubCode, genormaliseerd); }
        catch (Exception ex) { log.LogWarning("Wachtrijregel afhandelen mislukt na het aanmaken van een alias ({Fouttype})", ex.GetType().Name); }
    }

    /// <summary>Valideren of afwijzen; <paramref name="zetStatusAsync"/> krijgt (id, status, aanroeper) en geeft het aantal geraakte rijen.</summary>
    public static async Task<IActionResult> ValideerAsync(
        int id, string? body, LerenAanroeper wie, Func<int, string, LerenAanroeper, Task<int>> zetStatusAsync)
    {
        var status = LeesBody<StatusBody>(body)?.Status;
        if (status is not ("validated" or "rejected"))
            return new BadRequestObjectResult(new { error = "Ongeldige status. Gebruik 'validated' of 'rejected'." });

        var rijen = await zetStatusAsync(id, status, wie);
        return rijen == 0
            ? new NotFoundObjectResult(new { error = $"Teamalias {id} niet gevonden." })
            : new OkObjectResult(new { id, status });
    }

    /// <summary>
    /// Verwijderen. Het log bevat uitsluitend het alias-id: geen identificator van de beheerder
    /// (AVG: de audit staat alleen in de tabelkolommen, die met de rij worden bewaard en verdwijnen).
    /// </summary>
    public static async Task<IActionResult> VerwijderAsync(
        int id, ILogger log, Func<int, Task<int>> verwijderAsync)
    {
        var rijen = await verwijderAsync(id);
        if (rijen == 0) return new NotFoundObjectResult(new { error = $"Teamalias {id} niet gevonden." });
        log.LogInformation("Teamalias {AliasId} verwijderd", id);
        return new OkObjectResult(new { deleted = true, id });
    }

    /// <summary>Leest de volledige requestbody als tekst (de tier geeft die door aan de kernmethoden).</summary>
    public static async Task<string> LeesBodyAsync(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body);
        return await reader.ReadToEndAsync();
    }

    internal static T? LeesBody<T>(string? body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonConvert.DeserializeObject<T>(body); }
        catch (JsonException) { return null; }
    }

    private sealed class StatusBody { public string? Status { get; set; } }
}
