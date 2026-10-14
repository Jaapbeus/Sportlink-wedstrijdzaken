using Microsoft.Extensions.Logging;
using Planner.Endpoints.Email;
using Planner.Shared.Email;

namespace Planner.Endpoints.Tests.Email;

/// <summary>Testbericht voor de generieke fase-orkestratie (#1716 slice 9); testdata uit ARCHITECTUUR.md §8.1.5.</summary>
internal sealed record TestBericht(string MessageId, string Afzender = "trainer@voorbeeld.nl") : IInkomendBericht
{
    public string AfzenderNaam => "Jan de Vries";
    public string Onderwerp => "Test";
}

internal sealed class FakeGraph : IReplyGraph
{
    public List<(string To, string Subject, string Body)> Verstuurd { get; } = new();
    public bool GooiBijVersturen { get; set; }

    public Task SetCategoriesAsync(string messageId, params string[] categories) => Task.CompletedTask;
    public Task EnsureMasterCategoryAsync(string name, string colorPreset) => Task.CompletedTask;
    public Task MarkAsReadAsync(string messageId) => Task.CompletedTask;

    public Task SendReplyAsync(string to, string subject, string body, string? conversationId,
        IReadOnlyList<string>? bcc = null, EmailBijlage? bijlage = null)
    {
        if (GooiBijVersturen)
            throw new InvalidOperationException("SendReply gesimuleerde fout");
        Verstuurd.Add((to, subject, body));
        return Task.CompletedTask;
    }
}

internal sealed class FakeThrottle : INoodmailThrottlePoort
{
    private readonly Dictionary<string, DateTime> _verstuurdOp = new();

    public Task<DateTime?> LaatsteKeerVerstuurdAsync(string sleutel)
        => Task.FromResult(_verstuurdOp.TryGetValue(sleutel, out var waarde) ? waarde : (DateTime?)null);

    public Task RegistreerVerstuurdAsync(string sleutel, DateTime verstuurdOpUtc)
    {
        _verstuurdOp[sleutel] = verstuurdOpUtc;
        return Task.CompletedTask;
    }

    public Task WisAsync(string sleutel)
    {
        _verstuurdOp.Remove(sleutel);
        return Task.CompletedTask;
    }
}

/// <summary>Logger die de geformatteerde berichten bewaart (voor de #1143-controle: geen adressen in logs).</summary>
internal sealed class OpnameLogger : ILogger
{
    public List<string> Berichten { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Berichten.Add(formatter(state, exception));
}

/// <summary>
/// Programmeerbare tier-kant voor <see cref="EmailVerwerkerFasen"/>. Elke aanroep komt in
/// <see cref="Stappen"/>, zodat de tests de volgorde en de vroege stops kunnen controleren.
/// </summary>
internal sealed class FakePoort : IEmailVerwerkerPoort<TestBericht>
{
    public static readonly EmailNoodmailTeksten TestTeksten = new("TIER-DATABASEADVIES", "  • TIER-OPENAI-ACTIE\n");

    public FakeGraph FakeGraph { get; } = new();
    public FakeThrottle FakeThrottle { get; } = new();
    public List<string> Stappen { get; } = new();

    public List<TestBericht> Ongelezen { get; set; } = new();
    public Func<List<TestBericht>, List<TestBericht>> PreFilter { get; set; } = e => e;
    public HashSet<string> UitsluitingslijstVoorAi { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception? LijstVoorAiFout { get; set; }
    public bool AiBeschikbaar { get; set; } = true;
    public Func<List<TestBericht>, EmailClassificatieBatch<TestBericht>> Classificeer { get; set; }
        = l => new(l.Select(b => (b, new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek })).ToList(), false, null);
    public Exception? DatabaseFout { get; set; }
    public HashSet<string> UitsluitingslijstUitDatabase { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool VerwerkingGereed { get; set; } = true;
    public Func<TestBericht, Exception?> VerwerkFout { get; set; } = _ => null;
    public List<(string MessageId, IReadOnlySet<string> Uitgesloten)> Verwerkt { get; } = new();
    public List<(string MessageId, string FoutMelding)> VastgelegdeFouten { get; } = new();

    public IReplyGraph Graph => FakeGraph;
    public INoodmailThrottlePoort Throttle => FakeThrottle;
    public EmailNoodmailTeksten NoodmailTeksten => TestTeksten;

    public Task<List<TestBericht>> HaalOngelezenAsync()
    {
        Stappen.Add("haal");
        return Task.FromResult(Ongelezen);
    }

    public Task<List<TestBericht>> PreFilterAsync(List<TestBericht> emails, string eigenMailbox, IReadOnlySet<string> uitgeslotenCache)
    {
        Stappen.Add("prefilter");
        return Task.FromResult(PreFilter(emails));
    }

    public List<TestBericht> FilterUitgesloten(List<TestBericht> emails, IReadOnlySet<string> uitgeslotenAdressen)
    {
        Stappen.Add("filter");
        return emails.Where(e => !uitgeslotenAdressen.Contains(e.Afzender)).ToList();
    }

    public Task<HashSet<string>> WekEnLaadUitsluitingslijstAsync()
    {
        Stappen.Add("lijst-voor-ai");
        return LijstVoorAiFout is null ? Task.FromResult(UitsluitingslijstVoorAi) : Task.FromException<HashSet<string>>(LijstVoorAiFout);
    }

    public bool BereidAiVoor()
    {
        Stappen.Add("ai");
        return AiBeschikbaar;
    }

    public Task<EmailClassificatieBatch<TestBericht>> ClassificeerAsync(List<TestBericht> teClassificeren)
    {
        Stappen.Add("classificeer");
        return Task.FromResult(Classificeer(teClassificeren));
    }

    public Task LabelBuitenScopeAsync(List<(TestBericht Email, BerichtClassificatie Classificatie)> classificaties)
    {
        Stappen.Add("label");
        return Task.CompletedTask;
    }

    public Task WachtOpDatabaseAsync()
    {
        Stappen.Add("wacht");
        return DatabaseFout is null ? Task.CompletedTask : Task.FromException(DatabaseFout);
    }

    public Task LaadInstellingenAsync()
    {
        Stappen.Add("instellingen");
        return Task.CompletedTask;
    }

    public Task<HashSet<string>> LaadUitsluitingslijstAsync()
    {
        Stappen.Add("lijst");
        return Task.FromResult(UitsluitingslijstUitDatabase);
    }

    public Task<bool> BereidVerwerkingVoorAsync()
    {
        Stappen.Add("voorbereiding");
        return Task.FromResult(VerwerkingGereed);
    }

    public Task RegistreerClassificatieFoutAsync(TestBericht email)
    {
        Stappen.Add("classificatiefout:" + email.MessageId);
        return Task.CompletedTask;
    }

    public Task VerwerkEmailAsync(TestBericht email, BerichtClassificatie classificatie, IReadOnlySet<string> uitgeslotenAdressen)
    {
        Stappen.Add("verwerk:" + email.MessageId);
        var fout = VerwerkFout(email);
        if (fout is not null)
            return Task.FromException(fout);
        Verwerkt.Add((email.MessageId, uitgeslotenAdressen));
        return Task.CompletedTask;
    }

    public Task LegVerwerkingsFoutVastAsync(TestBericht email, string foutMelding)
    {
        Stappen.Add("fout:" + email.MessageId);
        VastgelegdeFouten.Add((email.MessageId, foutMelding));
        return Task.CompletedTask;
    }
}
