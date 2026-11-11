using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Planner.Endpoints.Http;
using Planner.Shared.Autorisatie;

namespace Planner.Endpoints.Autorisatie;

internal static class PlannerAanvragerEndpointCore
{
    internal sealed record ReadResult<T>(T Value, IActionResult? Error, DateOnly? Date = null, TimeOnly? Time = null);

    public static async Task<ReadResult<T>> ReadBevestigAsync<T>(HttpRequest req, string? requester)
        where T : class, IBevestigAanvragerRequest
    {
        var result = await ReadAsync<T>(req, requester,
            value => !string.IsNullOrEmpty(value.Datum) && !string.IsNullOrEmpty(value.AanvangsTijd),
            "Request body met 'datum', 'aanvangsTijd' en 'veldNummer' is verplicht.");
        if (result.Error is not null) return result;
        if (!DateOnly.TryParse(result.Value.Datum, out var date) || !TimeOnly.TryParse(result.Value.AanvangsTijd, out var time))
            return result with { Error = new BadRequestObjectResult(new { error = "Ongeldige datum of tijd." }) };
        return result with { Date = date, Time = time };
    }

    public static async Task<ReadResult<T>> ReadHerplanAsync<T>(HttpRequest req, string? requester)
        where T : class, IHerplanAanvragerRequest
    {
        var result = await ReadAsync<T>(req, requester,
            value => value.Wedstrijdcode != 0 && !string.IsNullOrEmpty(value.GewensteAanvangsTijd),
            "Request body met 'wedstrijdcode' en 'gewensteAanvangsTijd' is verplicht.");
        if (result.Error is not null) return result;
        if (!TimeOnly.TryParse(result.Value.GewensteAanvangsTijd, out var time))
            return result with { Error = new BadRequestObjectResult(new { error = "Ongeldige tijd." }) };
        return result with { Time = time };
    }

    private static async Task<ReadResult<T>> ReadAsync<T>(
        HttpRequest req, string? requester, Func<T, bool> valid, string requiredMessage)
        where T : class, IPlannerAanvragerRequest
    {
        // #1811: dezelfde begrensde UTF-8/JSON-lezing als de andere planner-endpoints (413 bij te groot,
        // 400 bij ongeldige JSON), zodat dit #1848-pad de body-limiet niet omzeilt.
        var read = await BoundedRequestBodyReader.ReadJsonAsync<T>(req, BoundedRequestBodyReader.DefaultJsonMaxBytes);
        if (!read.IsSuccess)
            return new ReadResult<T>(null!, BoundedRequestBodyReader.ToActionResult(read.Error));

        var value = read.Value;
        if (value is null || !valid(value))
            return new ReadResult<T>(null!, new BadRequestObjectResult(new { error = requiredMessage }));

        var failure = Apply(requester, actor => value.AangevraagdDoor = actor);
        return new ReadResult<T>(value, failure);
    }

    public static IActionResult? Apply(string? requester, Action<string> zetAanvrager)
    {
        if (requester is null)
            return new ObjectResult(new { error = "Aanvrager kan niet worden bepaald." }) { StatusCode = 403 };

        zetAanvrager(requester);
        return null;
    }
}
