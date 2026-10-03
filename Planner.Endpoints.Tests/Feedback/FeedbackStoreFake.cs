using Planner.Shared.Feedback;

namespace Planner.Endpoints.Tests.Feedback;

/// <summary>In-memory <see cref="IFeedbackStore"/> voor de endpoint-tests (#764): legt vast wat er bewaard, geclaimd en gelogd wordt.</summary>
internal sealed class FeedbackStoreFake : IFeedbackStore
{
    public List<(FeedbackNieuw Rij, IReadOnlyList<FeedbackTelemetrieRegel> Telemetrie)> Bewaard { get; } = [];
    public Dictionary<Guid, string> Statussen { get; } = [];
    public Dictionary<Guid, (int Nummer, string Url)> Gepubliceerd { get; } = [];
    public List<FeedbackInzageNieuw> Inzage { get; } = [];
    public FeedbackDetail? Detail { get; set; }
    public int RecentVoorGebruiker { get; set; }
    public int RecentVoorClub { get; set; }
    public bool BewarenFaalt { get; set; }
    public bool ClaimSlaagt { get; set; } = true;
    public FeedbackFilter? LaatsteFilter { get; private set; }

    public Task<int> TelRecenteMeldingenAsync(string clubCode, string? melderObjectId, DateTime sindsUtc) =>
        Task.FromResult(melderObjectId is null ? RecentVoorClub : RecentVoorGebruiker);

    public Task BewaarAsync(FeedbackNieuw nieuw, IReadOnlyList<FeedbackTelemetrieRegel> telemetrie)
    {
        if (BewarenFaalt) throw new InvalidOperationException("database onbereikbaar");
        Bewaard.Add((nieuw, telemetrie));
        Statussen[nieuw.FeedbackId] = nieuw.Status;
        return Task.CompletedTask;
    }

    public Task<bool> ClaimPublicatieAsync(string clubCode, Guid feedbackId)
    {
        if (ClaimSlaagt) Statussen[feedbackId] = FeedbackStatusWaarden.Publiceren;
        return Task.FromResult(ClaimSlaagt);
    }

    public Task ZetGepubliceerdAsync(string clubCode, Guid feedbackId, int issueNummer, string issueUrl)
    {
        Statussen[feedbackId] = FeedbackStatusWaarden.Gepubliceerd;
        Gepubliceerd[feedbackId] = (issueNummer, issueUrl);
        return Task.CompletedTask;
    }

    public Task ZetPublicatieMisluktAsync(string clubCode, Guid feedbackId)
    {
        Statussen[feedbackId] = FeedbackStatusWaarden.GitHubMislukt;
        return Task.CompletedTask;
    }

    public Task<FeedbackLijstResultaat> LijstAsync(string clubCode, FeedbackFilter filter)
    {
        LaatsteFilter = filter;
        return Task.FromResult(new FeedbackLijstResultaat([], 0));
    }

    public Task<FeedbackDetail?> GetDetailAsync(string clubCode, Guid feedbackId) =>
        Task.FromResult(Detail is not null && Detail.Samenvatting.FeedbackId == feedbackId ? Detail : null);

    public Task LogInzageAsync(FeedbackInzageNieuw inzage)
    {
        Inzage.Add(inzage);
        return Task.CompletedTask;
    }

    public Task<FeedbackInzageLijst> LijstInzageAsync(string clubCode, int limit, int offset) =>
        Task.FromResult(new FeedbackInzageLijst([], 0));

    public Task<IReadOnlyList<FeedbackIssueVerwijzing>> TeControlerenIssuesAsync(int max) =>
        Task.FromResult<IReadOnlyList<FeedbackIssueVerwijzing>>([]);

    public Task ZetIssueStatusAsync(Guid feedbackId, DateTime? geslotenOpUtc) => Task.CompletedTask;

    public Task<FeedbackRetentieResultaat> VoerRetentieUitAsync(DateTime nuUtc) =>
        Task.FromResult(new FeedbackRetentieResultaat(0, 0, 0));
}
