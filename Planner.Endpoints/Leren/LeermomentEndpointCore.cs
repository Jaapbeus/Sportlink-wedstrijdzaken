using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Email;
using Planner.Shared.Leren;

namespace Planner.Endpoints.Leren;

/// <summary>Body van <c>POST /api/beheer/leermomenten</c> (#1568 deel C).</summary>
public sealed class LeermomentAanmaakRequest
{
    /// <summary>Wat de pipeline ervan maakte; mag leeg (dan "Onbekend").</summary>
    public string? OrigineelVerzoekType { get; set; }
    public string? JuistVerzoekType { get; set; }
    /// <summary>Door de beheerder geredigeerde, korte omschrijving; wordt door de PII-arme sanering gehaald.</summary>
    public string? Samenvatting { get; set; }
    public int? HerkomstVerwerkingId { get; set; }
}

/// <summary>Een leermoment door een beheerder: direct gevalideerd, herkomst <c>Admin</c>, nooit door de cleanup geraakt.</summary>
public static class LeermomentEndpointCore
{
    public const int StandaardLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>Leest <c>status</c> en <c>limit</c> van de lijstaanroep; de limiet is begrensd op <see cref="MaxLimit"/>.</summary>
    public static (string Status, int Limit) LeesLijstFilter(IQueryCollection query)
        => (query["status"].ToString(),
            int.TryParse(query["limit"].ToString(), out var l) ? Math.Min(MaxLimit, Math.Max(1, l)) : StandaardLimit);

    /// <summary>Valideren of afwijzen; <paramref name="valideerAsync"/> krijgt (id, isGevalideerd, isAfgewezen) en geeft het aantal geraakte rijen.</summary>
    public static async Task<IActionResult> ValideerAsync(int id, string? body, Func<int, bool, bool, Task<int>> valideerAsync)
    {
        var actie = TeamAliasEndpointCore.LeesBody<ActieBody>(body)?.Actie;
        if (actie is not ("valideer" or "afwijzen"))
            return new BadRequestObjectResult(new { error = "Ongeldige actie. Gebruik 'valideer' of 'afwijzen'." });

        var rijen = await valideerAsync(id, actie == "valideer", actie == "afwijzen");
        return rijen == 0
            ? new NotFoundObjectResult(new { error = $"Leermoment {id} niet gevonden." })
            : new OkObjectResult(new { id, actie });
    }

    private sealed class ActieBody { public string? Actie { get; set; } }

    public static async Task<IActionResult> AanmakenAsync(
        string clubCode, string? body, LerenAanroeper wie, Func<AdminLeermomentOpdracht, Task<int>> maakAanAsync)
    {
        var dto = TeamAliasEndpointCore.LeesBody<LeermomentAanmaakRequest>(body);
        if (dto is null)
            return new BadRequestObjectResult(new { error = "Juist verzoektype en samenvatting zijn verplicht." });

        var (waarde, fout) = LeermomentInvoer.Valideer(dto.OrigineelVerzoekType, dto.JuistVerzoekType, dto.Samenvatting);
        if (waarde is null) return new BadRequestObjectResult(new { error = fout });

        var id = await maakAanAsync(new AdminLeermomentOpdracht(
            clubCode, waarde.OrigineelType, waarde.JuistType, waarde.Samenvatting, wie, dto.HerkomstVerwerkingId));
        return new ObjectResult(new { id, status = "validated", herkomst = "Admin", samenvatting = waarde.Samenvatting }) { StatusCode = 201 };
    }
}
