using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

/// <summary>
/// Tests voor de twee delen van de deduplicatie en voor de opbouw van de gepubliceerde
/// issue-/comment-body (#1268).
///
/// <para>
/// Waarom deze tests er vóór #1268 niet waren: de 24-uurs rate-limiting zat als <c>lock</c>-blok
/// midden in <c>ReportAsync</c>, en de fingerprintberekening stond in <c>SystemUtilities</c> van de
/// SQL Server-tier. Geen van beide was te raken zonder een echte GitHub-aanroep te doen. Bij het
/// delen van de klasse zijn ze losgetrokken tot <c>ProbeerRegistreren</c> en
/// <c>ComputeFingerprint</c>, zodat de dedup-beslissing zelf bewijsbaar is.
/// </para>
///
/// <para>
/// Elke test gebruikt een eigen, unieke fingerprint. De rate-limit-tabel is statisch (hij moet een
/// koude start van de Function App overleven binnen één proces), dus tests die dezelfde waarde
/// zouden delen, zouden elkaar beïnvloeden.
/// </para>
/// </summary>
public class GitHubIssueReporterDeduplicatieTests
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";

    // ---------------------------------------------------------------------------------------
    // Deel 1 — in-memory rate-limiting (#106)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ProbeerRegistreren_EersteKeer_MagRapporteren()
    {
        var nu = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

        GitHubIssueReporter.ProbeerRegistreren("fp-eerste-keer", nu)
            .Should().BeTrue("een nog niet eerder gemelde fingerprint moet gerapporteerd worden");
    }

    [Fact]
    public void ProbeerRegistreren_TweedeKeerBinnen24Uur_WordtOvergeslagen()
    {
        const string fp = "fp-binnen-24u";
        var nu = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

        GitHubIssueReporter.ProbeerRegistreren(fp, nu).Should().BeTrue();

        GitHubIssueReporter.ProbeerRegistreren(fp, nu.AddHours(23).AddMinutes(59))
            .Should().BeFalse("binnen het venster van 24 uur mag dezelfde fout geen tweede melding opleveren");
    }

    [Fact]
    public void ProbeerRegistreren_NaVierentwintigUur_MagOpnieuw()
    {
        const string fp = "fp-na-24u";
        var nu = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

        GitHubIssueReporter.ProbeerRegistreren(fp, nu).Should().BeTrue();

        GitHubIssueReporter.ProbeerRegistreren(fp, nu.AddHours(24))
            .Should().BeTrue("precies op de grens van 24 uur is het venster verlopen");
    }

    [Fact]
    public void ProbeerRegistreren_TweedeMeldingSchuiftHetVensterOp()
    {
        const string fp = "fp-venster-schuift";
        var nu = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

        GitHubIssueReporter.ProbeerRegistreren(fp, nu).Should().BeTrue();
        GitHubIssueReporter.ProbeerRegistreren(fp, nu.AddHours(24)).Should().BeTrue();

        GitHubIssueReporter.ProbeerRegistreren(fp, nu.AddHours(25))
            .Should().BeFalse("na de tweede melding begint een nieuw venster van 24 uur");
    }

    [Fact]
    public void ProbeerRegistreren_AndereFingerprint_WordtNietGeblokkeerd()
    {
        var nu = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

        GitHubIssueReporter.ProbeerRegistreren("fp-onafhankelijk-a", nu).Should().BeTrue();

        GitHubIssueReporter.ProbeerRegistreren("fp-onafhankelijk-b", nu)
            .Should().BeTrue("de rate-limiting geldt per fingerprint, niet globaal — anders verdwijnt een tweede, andere fout");
    }

    // ---------------------------------------------------------------------------------------
    // Deel 2 — fingerprintberekening: de sleutel waarop de dedup-lookup matcht
    // ---------------------------------------------------------------------------------------

    private const string TierPrefix = "Planner.Shared.Tests.";
    private const string AndereTierPrefix = "EenAndereTier.";

    private static Exception GooiEnVang(string bericht)
    {
        try
        {
            throw new InvalidOperationException(bericht);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [Fact]
    public void ComputeFingerprint_ZelfdeFout_GeeftZelfdeFingerprint()
    {
        var a = GooiEnVang("Verbinding met de database mislukt");
        var b = GooiEnVang("Verbinding met de database mislukt");

        GitHubIssueReporter.ComputeFingerprint(a, TierPrefix)
            .Should().Be(GitHubIssueReporter.ComputeFingerprint(b, TierPrefix),
                "zonder deterministische fingerprint matcht de dedup-lookup nooit op een bestaand issue");
    }

    [Fact]
    public void ComputeFingerprint_IsTwaalfHexTekens()
    {
        var fp = GitHubIssueReporter.ComputeFingerprint(GooiEnVang("iets"), TierPrefix);

        fp.Should().MatchRegex("^[0-9a-f]{12}$",
            "de fingerprint staat in de issue-titel als [fp:...] en moet daar exact herkenbaar zijn");
    }

    [Theory]
    [InlineData("Record 12345 niet gevonden", "Record 98765 niet gevonden")]
    [InlineData("Mislukt op 2026-09-19", "Mislukt op 2025-01-02")]
    [InlineData(
        "Sessie 3f2504e0-4f89-11d3-9a0c-0305e82c3301 verlopen",
        "Sessie 0e8a1b2c-4f89-11d3-9a0c-0305e82c3301 verlopen")]
    public void ComputeFingerprint_VariabeleDelenInBericht_GevenZelfdeFingerprint(string berichtA, string berichtB)
    {
        // Getallen, datums en GUID's worden genormaliseerd — anders krijgt dezelfde terugkerende
        // fout elke keer een nieuwe fingerprint en dus een nieuw issue.
        GitHubIssueReporter.ComputeFingerprint(GooiEnVang(berichtA), TierPrefix)
            .Should().Be(GitHubIssueReporter.ComputeFingerprint(GooiEnVang(berichtB), TierPrefix));
    }

    [Fact]
    public void ComputeFingerprint_AnderBericht_GeeftAndereFingerprint()
    {
        var a = GitHubIssueReporter.ComputeFingerprint(GooiEnVang("Veld ontbreekt"), TierPrefix);
        var b = GitHubIssueReporter.ComputeFingerprint(GooiEnVang("Team ontbreekt"), TierPrefix);

        a.Should().NotBe(b, "twee inhoudelijk verschillende fouten mogen niet op één issue samenvallen");
    }

    [Fact]
    public void ComputeFingerprint_AnderExceptietype_GeeftAndereFingerprint()
    {
        Exception argument;
        try { throw new ArgumentException("zelfde tekst"); } catch (ArgumentException ex) { argument = ex; }

        GitHubIssueReporter.ComputeFingerprint(argument, TierPrefix)
            .Should().NotBe(GitHubIssueReporter.ComputeFingerprint(GooiEnVang("zelfde tekst"), TierPrefix));
    }

    [Fact]
    public void ComputeFingerprint_NamespaceVoorvoegselBepaaltDeCallsite()
    {
        // Dit is de reden dat het voorvoegsel een parameter is en geen gedeelde constante (#1268):
        // het bepaalt welke stackframe als "eigen code" telt en dus mee-hasht. Eén gedeelde lijst
        // met beide tier-voorvoegsels zou de fingerprints van de bestaande tier verschuiven en
        // daarmee de dedup van reeds aangemaakte issues breken.
        var ex = GooiEnVang("zelfde fout, andere tier-bril");

        GitHubIssueReporter.ComputeFingerprint(ex, TierPrefix)
            .Should().NotBe(GitHubIssueReporter.ComputeFingerprint(ex, AndereTierPrefix));
    }

    [Fact]
    public void ComputeFingerprint_ExceptieZonderStacktrace_WerktZonderFout()
    {
        // Een exception die nooit gegooid is heeft StackTrace == null. Dat mag de rapportage niet
        // laten omvallen — de fingerprint valt dan terug op "unknown" als callsite.
        var nooitGegooid = new InvalidOperationException("nooit gegooid");

        GitHubIssueReporter.ComputeFingerprint(nooitGegooid, TierPrefix)
            .Should().MatchRegex("^[0-9a-f]{12}$");
    }

    // ---------------------------------------------------------------------------------------
    // Deel 3 — opbouw van de gepubliceerde issue-/comment-body
    // ---------------------------------------------------------------------------------------

    /// <summary>Simuleert een Npgsql-exception; het echte type is in een unit test niet te
    /// instantiëren. De typenaam is wat de classificatie leest.</summary>
    private sealed class NpgsqlPostgresException : Exception
    {
        public NpgsqlPostgresException(string message) : base(message) { }
    }

    [Fact]
    public void BuildPublicTitle_NpgsqlAchtigeException_ClassificeertAlsDatabase()
    {
        // De classificatie kent geen Postgres-tak: ze toetst op "Sql" in de typenaam. "Npgsql"
        // bevat dat hoofdletterongevoelig, dus de Postgres-tier krijgt dezelfde categorie als de
        // SQL Server-tier. Dat was tot #1268 nergens vastgelegd en dus een aanname.
        GitHubIssueReporter.BuildPublicTitle(new NpgsqlPostgresException("relation does not exist"), "abc123abc123")
            .Should().Contain("Database");
    }

    [Fact]
    public void BuildPublicTitle_HeeftVasteVormMetFingerprintTag()
    {
        // De dedup-lookup zoekt letterlijk op "[fp:{fp}]" in de titel. Wijzigt deze vorm, dan
        // vindt SearchIssueAsync geen enkel bestaand issue meer en loopt de repo vol duplicaten.
        var titel = GitHubIssueReporter.BuildPublicTitle(new FormatException("x"), "abc123abc123");

        titel.Should().StartWith("[bug][fp:abc123abc123] ");
        titel.Should().EndWith(nameof(FormatException));
    }

    [Fact]
    public void BuildPublicDiagnostics_BevatGeenStacktraceOfBronpad()
    {
        // CISO/#1008: alleen de vaste allowlist-velden. Een echte, gegooide exception heeft een
        // stacktrace met bestandspaden van de buildmachine; die mag nergens in de publieke tekst
        // opduiken.
        var ex = GooiEnVang("fout met stacktrace");

        var blok = GitHubIssueReporter.BuildPublicDiagnostics(
            ex, "PostgresFetchAndStoreApiData", "abc123abc123", new DateTime(2026, 9, 19, 14, 30, 0));

        blok.Should().NotContain("   at ");
        blok.Should().NotContain(".cs:line");
        blok.Should().NotContain("fout met stacktrace");
        blok.Should().Contain("PostgresFetchAndStoreApiData");
        blok.Should().Contain("19-09-2026 14:30");
    }

    [Fact]
    public async Task CreateIssueAsync_VerstuurtDeLabelsWaaropDeDedupLookupFiltert()
    {
        // SearchIssueAsync zoekt met labels=bug. Een nieuw issue dat dat label niet krijgt, wordt
        // bij een volgende melding nooit teruggevonden — de dedup zou stil stukgaan.
        string? verzondenBody = null;
        var client = MaakClient(req =>
        {
            verzondenBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"number": 1}""") };
        });

        await GitHubIssueReporter.CreateIssueAsync(
            client, Owner, Repo, "abc123abc123", new FormatException("x"),
            "PostgresSyncMatchesHttp", NullLogger.Instance);

        verzondenBody.Should().NotBeNull();
        verzondenBody.Should().Contain("\"bug\"");
        verzondenBody.Should().Contain("type: bug");
        verzondenBody.Should().Contain("[fp:abc123abc123]");
    }

    [Fact]
    public async Task AddCommentAsync_MarkeertDeMeldingAlsHerhaling()
    {
        string? verzondenBody = null;
        var client = MaakClient(req =>
        {
            verzondenBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        await GitHubIssueReporter.AddCommentAsync(
            client, Owner, Repo, 42, new FormatException("x"),
            "PostgresFetchAndStoreApiData", "abc123abc123", NullLogger.Instance);

        verzondenBody.Should().NotBeNull();
        verzondenBody.Should().Contain("Opnieuw opgetreden");
        verzondenBody.Should().Contain("PostgresFetchAndStoreApiData");
    }

    // ---------------------------------------------------------------------------------------
    // Deel 4 — de EgressGuard-poort (#857) is de eerste beslissing van ReportAsync
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ReportAsync_EgressGeblokkeerd_DoetNiets()
    {
        // CLAUDE.md "Uitgaande integraties": de GitHub-aanroep is uitgaand verkeer en mag buiten
        // productie nooit vertrekken. De guard zit per tier; hier komt hij als delegate binnen.
        // Deze test bewijst dat die delegate ook echt leidend is — en dat ReportAsync bij een
        // gesloten poort niet eens aan de omgevingsvariabelen komt.
        var log = new OpvangLogger();

        await GitHubIssueReporter.ReportAsync(
            new FormatException("x"), "PostgresFetchAndStoreApiData", log,
            egressToegestaan: () => false, eigenNamespacePrefix: TierPrefix);

        log.Regels.Should().ContainSingle();
        log.Regels[0].Should().Contain("EgressGuard");
    }

    private sealed class OpvangLogger : ILogger
    {
        public List<string> Regels { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Regels.Add(formatter(state, exception));
    }

    private static HttpClient MaakClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => respond(req));
        return new HttpClient(handler.Object);
    }
}
