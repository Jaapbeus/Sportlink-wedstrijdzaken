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
    public void NaarUitzondering_DraagtFasesInHetBerichtEnIsInvalidOperation()
    {
        var u = new SyncUitkomst();
        u.Registreer(SyncUitkomst.FaseMatchdetails, true);

        var ex = u.NaarUitzondering();

        ex.Fases.Should().Equal("matchdetails");
        ex.Message.Should().Contain("matchdetails");
        ex.Should().BeAssignableTo<InvalidOperationException>();
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
