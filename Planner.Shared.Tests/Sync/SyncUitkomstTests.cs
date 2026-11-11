using AwesomeAssertions;
using Planner.Shared.Infrastructure;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

/// <summary>Getypeerde "sync gedeeltelijk mislukt"-melding met faseoverzicht en eigen categorie (#1779).</summary>
public class SyncUitkomstTests
{
    [Fact]
    public void ZonderMislukteFase_IsGeslaagd()
    {
        var u = new SyncUitkomst();
        u.Registreer(SyncUitkomst.FaseTeams, false).Should().BeFalse();
        u.GedeeltelijkMislukt.Should().BeFalse();
        u.MislukteFases.Should().BeEmpty();
    }

    [Fact]
    public void MislukteFases_WordenEenmaligEnInVolgordeBijgehouden()
    {
        var u = new SyncUitkomst();
        u.Registreer(SyncUitkomst.FaseProgramma, true).Should().BeTrue();
        u.Registreer(SyncUitkomst.FaseTeams, false);
        u.Registreer(SyncUitkomst.FaseProgramma, true);
        u.Registreer(SyncUitkomst.FaseMatchdetails, true);

        u.GedeeltelijkMislukt.Should().BeTrue();
        u.MislukteFases.Should().Equal("programma", "matchdetails");
    }

    [Fact]
    public void NaarUitzondering_HeeftVastBerichtEnFasesInEigenschap()
    {
        var u = new SyncUitkomst();
        u.Registreer(SyncUitkomst.FaseMatchdetails, true);

        var ex = u.NaarUitzondering();

        ex.Fases.Should().Equal("matchdetails");
        ex.Message.Should().Be(SyncGedeeltelijkMisluktException.Bericht);
        ex.Should().BeAssignableTo<InvalidOperationException>();
    }

    // ── Review P3: één fingerprint per foutsoort, niet per faseset ─────────────────────────────
    private static Exception GooiVanuitDezelfdePlek(params string[] fases)
    {
        var u = new SyncUitkomst();
        foreach (var fase in fases)
            u.Registreer(fase, true);
        try
        {
            u.GooiAlsMislukt();
            throw new InvalidOperationException("niet verwacht: GooiAlsMislukt gooide niet");
        }
        catch (SyncGedeeltelijkMisluktException ex)
        {
            return ex;
        }
    }

    [Fact]
    public void Fingerprint_IsGelijkVoorVerschillendeFasesets()
    {
        // ComputeFingerprint hasht type + bericht + aanroepplek. Stond de faselijst in het bericht,
        // dan opende elke combinatie (tot 31) een eigen publiek issue.
        const string prefix = "Planner.Shared.Tests.";
        var alleenMatchdetails = GooiVanuitDezelfdePlek(SyncUitkomst.FaseMatchdetails);
        var teamsEnMatchdetails = GooiVanuitDezelfdePlek(SyncUitkomst.FaseTeams, SyncUitkomst.FaseMatchdetails);

        GitHubIssueReporter.ComputeFingerprint(alleenMatchdetails, prefix)
            .Should().Be(GitHubIssueReporter.ComputeFingerprint(teamsEnMatchdetails, prefix));
        ((SyncGedeeltelijkMisluktException)teamsEnMatchdetails).Fases.Should().Equal("teams", "matchdetails");
    }

    [Fact]
    public void GooiAlsMislukt_ZonderMislukteFase_GooitNiet()
    {
        var act = () => new SyncUitkomst().GooiAlsMislukt();
        act.Should().NotThrow();
    }

    [Fact]
    public void GooiAlsMislukt_MetMislukteFase_GooitGetypeerdeFout()
    {
        var act = () => SyncUitkomst.Configuratie().GooiAlsMislukt();
        act.Should().Throw<SyncGedeeltelijkMisluktException>()
            .Which.Fases.Should().Equal("configuratie");
    }

    [Fact]
    public void Uitzondering_KopieertFases_LatereRegistratieWijzigtHemNiet()
    {
        var u = new SyncUitkomst();
        u.Registreer(SyncUitkomst.FaseTeams, true);
        var ex = u.NaarUitzondering();
        u.Registreer(SyncUitkomst.FaseUitslagen, true);

        ex.Fases.Should().Equal("teams");
    }

    [Fact]
    public void Foutrapportage_ToontEigenCategorieEnFases_NietConfiguratieStatus()
    {
        var u = new SyncUitkomst();
        u.Registreer(SyncUitkomst.FaseMatchdetails, true);
        var ex = u.NaarUitzondering();

        var titel = GitHubIssueReporter.BuildPublicTitle(ex, "abc123");
        var blok = GitHubIssueReporter.BuildPublicDiagnostics(ex, "FetchAndStoreApiData", "abc123", DateTime.UtcNow);

        titel.Should().Contain("Sync gedeeltelijk mislukt").And.NotContain("Configuratie/status");
        blok.Should().Contain("**Foutcategorie:** Sync gedeeltelijk mislukt")
            .And.Contain("**Mislukte fases:** matchdetails");
    }

    [Fact]
    public void GewoneInvalidOperation_BlijftConfiguratieStatus_ZonderFasesRegel()
    {
        var ex = new InvalidOperationException("x");

        GitHubIssueReporter.BuildPublicTitle(ex, "abc123").Should().Contain("Configuratie/status");
        GitHubIssueReporter.BuildPublicDiagnostics(ex, "F", "abc123", DateTime.UtcNow).Should().NotContain("Mislukte fases");
    }

    [Fact]
    public void JobFoutmelding_BevatAlleenVasteFaselabels()
    {
        var u = new SyncUitkomst();
        u.Registreer(SyncUitkomst.FaseUitslagen, true);

        u.JobFoutmelding().Should().Contain("uitslagen");
    }

    [Fact]
    public void Configuratie_IsGedeeltelijkMislukt()
        => SyncUitkomst.Configuratie().MislukteFases.Should().Equal("configuratie");
}
