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
        if (ClaimSlaagt)
        {
            Statussen[feedbackId] = FeedbackStatusWaarden.Publiceren;
            PublicatieGestart[feedbackId] = ClaimSlot[feedbackId] = DateTime.UtcNow;
        }
        return Task.FromResult(ClaimSlaagt);
    }

    /// <summary>Begin van de publicatiepoging (kolom <c>PublicatieGestartOpUtc</c>): alleen <see cref="ClaimPublicatieAsync"/> zet het.</summary>
    public Dictionary<Guid, DateTime> PublicatieGestart { get; } = [];

    /// <summary>Het slot (kolom <c>mta_modified</c>): claim én herclaim verversen het.</summary>
    public Dictionary<Guid, DateTime> ClaimSlot { get; } = [];

    /// <summary>Begin van de poging voor een rij zonder vastgelegd begin (geclaimd vóór de kolom); <c>null</c> = herclaim weigert.</summary>
    public DateTime? HerClaimVorigMoment { get; set; } = DateTime.UtcNow.AddMinutes(-10);

    public Task<DateTime?> HerClaimPublicatieAsync(string clubCode, Guid feedbackId, DateTime claimOuderDanUtc)
    {
        if (Statussen.GetValueOrDefault(feedbackId) != FeedbackStatusWaarden.Publiceren) return Task.FromResult<DateTime?>(null);
        if (ClaimSlot.TryGetValue(feedbackId, out var slot) && slot >= claimOuderDanUtc) return Task.FromResult<DateTime?>(null);
        var gestart = PublicatieGestart.TryGetValue(feedbackId, out var g) ? g : HerClaimVorigMoment;
        if (gestart is null) return Task.FromResult<DateTime?>(null);
        ClaimSlot[feedbackId] = DateTime.UtcNow;
        return Task.FromResult(gestart);
    }

    /// <summary>Laat tijd verstrijken: alle vastgelegde momenten schuiven terug, zodat een vers slot weer verloopt.</summary>
    public void VerstrijkTijd(TimeSpan duur)
    {
        foreach (var id in ClaimSlot.Keys.ToList()) ClaimSlot[id] -= duur;
        foreach (var id in PublicatieGestart.Keys.ToList()) PublicatieGestart[id] -= duur;
    }

    public List<(Guid Id, string Body)> IssueBodyWijzigingen { get; } = [];
    public bool IssueBodyBewarenFaalt { get; set; }

    public Task<bool> ZetIssueBodyAsync(string clubCode, Guid feedbackId, string issueBody)
    {
        if (IssueBodyBewarenFaalt) throw new InvalidOperationException("database onbereikbaar");
        if (Statussen.GetValueOrDefault(feedbackId) != FeedbackStatusWaarden.Publiceren) return Task.FromResult(false);
        IssueBodyWijzigingen.Add((feedbackId, issueBody));
        return Task.FromResult(true);
    }

    /// <summary>Zoveel volgende receipt-opslagen mislukken (database onbereikbaar).</summary>
    public int ReceiptFaaltKeren { get; set; }

    public Task ZetGepubliceerdAsync(string clubCode, Guid feedbackId, int issueNummer, string issueUrl)
    {
        if (ReceiptFaaltKeren > 0)
        {
            ReceiptFaaltKeren--;
            throw new InvalidOperationException("database onbereikbaar");
        }
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

    /// <summary>Het detail zoals de database het nu zou geven: met de bijgehouden status en de laatst bewaarde issuetekst.</summary>
    public Task<FeedbackDetail?> GetDetailAsync(string clubCode, Guid feedbackId)
    {
        if (Detail is null || Detail.Samenvatting.FeedbackId != feedbackId) return Task.FromResult<FeedbackDetail?>(null);
        var status = Statussen.TryGetValue(feedbackId, out var s) ? s : Detail.Samenvatting.Status;
        var body = IssueBodyWijzigingen.LastOrDefault(w => w.Id == feedbackId).Body ?? Detail.IssueBody;
        return Task.FromResult<FeedbackDetail?>(Detail with { Samenvatting = Detail.Samenvatting with { Status = status }, IssueBody = body });
    }

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
