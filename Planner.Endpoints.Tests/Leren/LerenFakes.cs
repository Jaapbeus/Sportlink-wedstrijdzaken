using Planner.Shared.Leren;

namespace Planner.Endpoints.Tests.Leren;

internal sealed class FakeAliasStore : ITeamAliasStore
{
    public AliasAanmaakUitkomst Uitkomst { get; set; } = new(AliasAanmaakStatus.Aangemaakt, 11, "TESTCLUB O10-4");
    public AliasAanmaakOpdracht? Opdracht { get; private set; }

    public Task<AliasAanmaakUitkomst> MaakAanAsync(AliasAanmaakOpdracht opdracht)
    {
        Opdracht = opdracht;
        return Task.FromResult(Uitkomst);
    }
}

internal sealed class FakeWachtrij : IOnbekendeTeamTekstStore
{
    public List<(string Club, string Sleutel)> Afgehandeld { get; } = new();
    public bool FaalBijAfhandelen { get; set; }
    public List<OnbekendeTeamTekstRij> Rijen { get; } = new();
    public (int Id, string Status)? LaatsteStatus { get; private set; }
    public string? LaatsteLijstStatus { get; private set; }
    public int AantalRijenGeraakt { get; set; } = 1;

    public Task VoegToeAsync(string clubCode, string genormaliseerd, string voorbeeld, int verwerkingId) => Task.CompletedTask;

    public Task<IReadOnlyList<OnbekendeTeamTekstRij>> LijstAsync(string clubCode, string? status, int limit)
    {
        LaatsteLijstStatus = status;
        return Task.FromResult<IReadOnlyList<OnbekendeTeamTekstRij>>(Rijen);
    }

    public Task<int> AantalOpenAsync(string clubCode) => Task.FromResult(Rijen.Count(r => r.Status == "open"));

    public Task<int> ZetStatusAsync(string clubCode, int id, string status)
    {
        LaatsteStatus = (id, status);
        return Task.FromResult(AantalRijenGeraakt);
    }

    public Task<int> MarkeerAfgehandeldAsync(string clubCode, string genormaliseerd)
    {
        if (FaalBijAfhandelen) throw new InvalidOperationException("database weg");
        Afgehandeld.Add((clubCode, genormaliseerd));
        return Task.FromResult(1);
    }
}

internal sealed class FakeLeermomentStore
{
    public AdminLeermomentOpdracht? Opdracht { get; private set; }

    /// <summary>De delegate die de endpointkern als opslag meekrijgt.</summary>
    public Task<int> MaakAanAsync(AdminLeermomentOpdracht opdracht)
    {
        Opdracht = opdracht;
        return Task.FromResult(5);
    }
}
