using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>#1552: een vertraagd opslagresultaat verandert uitsluitend de bewerksessie die het startte.</summary>
public class SpeeltijdBewerkingTests
{
    private static readonly SpeeltijdDto A = new() { Leeftijd = "JO10", WedstrijdTotaal = 65 };
    private static readonly SpeeltijdDto B = new() { Leeftijd = "JO11", WedstrijdTotaal = 75 };

    private static (Task<bool> opslag, TaskCompletionSource<ApiResult<object>> antwoord) StartVertraagdeOpslag(SpeeltijdBewerking bewerking)
    {
        var antwoord = new TaskCompletionSource<ApiResult<object>>();
        return (bewerking.OpslaanAsync((_, _) => antwoord.Task), antwoord);
    }

    [Fact]
    public async Task DirecteFout_StaatBijDeEigenSessie()
    {
        var bewerking = new SpeeltijdBewerking();
        bewerking.StartBewerken(A);

        var opgeslagen = await bewerking.OpslaanAsync((_, _) => Task.FromResult(ApiResult<object>.Fail("ongeldig", 400)));

        opgeslagen.Should().BeFalse();
        bewerking.Actief!.Fout.Should().Be("ongeldig");
        bewerking.Melding.Should().BeNull();
    }

    [Fact]
    public async Task DirectSucces_SluitHetFormulier()
    {
        var bewerking = new SpeeltijdBewerking();
        bewerking.StartBewerken(A);

        (await bewerking.OpslaanAsync((_, _) => Task.FromResult(ApiResult<object>.Ok(new object())))).Should().BeTrue();

        bewerking.Actief.Should().BeNull();
    }

    [Fact]
    public async Task VertraagdeFout_NaWisselenNaarB_KomtNietBijB()
    {
        var bewerking = new SpeeltijdBewerking();
        bewerking.StartBewerken(A);
        var (opslag, antwoord) = StartVertraagdeOpslag(bewerking);

        bewerking.StartBewerken(B);
        var sessieB = bewerking.Actief!;
        antwoord.SetResult(ApiResult<object>.Fail("serverfout"));
        (await opslag).Should().BeFalse();

        bewerking.Actief.Should().BeSameAs(sessieB);
        sessieB.Fout.Should().BeNull();
        bewerking.Melding.Should().Be("Opslaan van JO10 is mislukt: serverfout");
    }

    [Fact]
    public async Task VertraagdSucces_NaWisselenNaarB_SluitBNiet()
    {
        var bewerking = new SpeeltijdBewerking();
        bewerking.StartBewerken(A);
        var (opslag, antwoord) = StartVertraagdeOpslag(bewerking);

        bewerking.StartBewerken(B);
        var sessieB = bewerking.Actief!;
        antwoord.SetResult(ApiResult<object>.Ok(new object()));

        (await opslag).Should().BeTrue("de lijst moet alsnog ververst worden");
        bewerking.Actief.Should().BeSameAs(sessieB);
        bewerking.Melding.Should().BeNull();
    }

    [Fact]
    public async Task VertraagdResultaat_NaNieuweCategorie_RaaktHetNieuweFormulierNiet()
    {
        var bewerking = new SpeeltijdBewerking();
        bewerking.StartBewerken(A);
        var (opslag, antwoord) = StartVertraagdeOpslag(bewerking);

        bewerking.StartNieuw();
        var nieuw = bewerking.Actief!;
        antwoord.SetResult(ApiResult<object>.Fail("serverfout"));
        await opslag;

        bewerking.Actief.Should().BeSameAs(nieuw);
        nieuw.IsNieuw.Should().BeTrue();
        nieuw.Fout.Should().BeNull();
        bewerking.Melding.Should().Contain("JO10");
    }

    [Fact]
    public async Task VertraagdeFout_NaAnnuleren_WordtMelding()
    {
        var bewerking = new SpeeltijdBewerking();
        bewerking.StartBewerken(A);
        var (opslag, antwoord) = StartVertraagdeOpslag(bewerking);

        bewerking.Annuleer();
        antwoord.SetResult(ApiResult<object>.Fail("serverfout"));
        await opslag;

        bewerking.Actief.Should().BeNull();
        bewerking.Melding.Should().Be("Opslaan van JO10 is mislukt: serverfout");
        bewerking.SluitMelding();
        bewerking.Melding.Should().BeNull();
    }

    [Fact]
    public async Task HeropenenTijdensLopendeOpslag_WordtGeweigerd_EnDaarnaWeerToegestaan()
    {
        var bewerking = new SpeeltijdBewerking();
        bewerking.StartBewerken(A);
        bewerking.Actief!.Model.WedstrijdTotaal = 90;
        var (opslag, antwoord) = StartVertraagdeOpslag(bewerking);
        bewerking.Annuleer();

        bewerking.IsOpslagBezig(A).Should().BeTrue();
        bewerking.StartBewerken(A).Should().BeFalse("de kopie zou de waarde van vóór de lopende opslag bevatten");
        bewerking.Actief.Should().BeNull();
        bewerking.IsOpslagBezig(B).Should().BeFalse();
        bewerking.StartBewerken(B).Should().BeTrue("een andere categorie wordt niet geraakt");

        antwoord.SetResult(ApiResult<object>.Ok(new object()));
        (await opslag).Should().BeTrue();

        bewerking.IsOpslagBezig(A).Should().BeFalse();
        bewerking.Actief!.Model.Leeftijd.Should().Be("JO11", "de sessie van B blijft open");
        bewerking.StartBewerken(A).Should().BeTrue();
        bewerking.IsInBewerking(A).Should().BeTrue();
    }

    [Fact]
    public async Task OpslagBezig_GeldtAlleenVoorDeRegelDieWordtOpgeslagen_OokNaWisselen()
    {
        var bewerking = new SpeeltijdBewerking();
        bewerking.StartBewerken(A);
        var (opslag, antwoord) = StartVertraagdeOpslag(bewerking);
        bewerking.StartBewerken(B);

        bewerking.IsOpslagBezig(A).Should().BeTrue();
        bewerking.IsOpslagBezig(B).Should().BeFalse();

        antwoord.SetResult(ApiResult<object>.Fail("x"));
        await opslag;

        bewerking.IsOpslagBezig(A).Should().BeFalse("ook een mislukte opslag geeft de regel vrij");
    }

    [Fact]
    public async Task NieuweCategorieInOpslag_BlokkeertGeenBestaandeRegelMetDieNaam()
    {
        var bewerking = new SpeeltijdBewerking();
        bewerking.StartNieuw();
        bewerking.Actief!.Model.Leeftijd = "JO10";
        var (opslag, antwoord) = StartVertraagdeOpslag(bewerking);

        bewerking.IsOpslagBezig(A).Should().BeFalse("een POST van een nieuwe categorie is geen opslag van de bestaande regel");

        antwoord.SetResult(ApiResult<object>.Fail("bestaat al", 409));
        await opslag;
        bewerking.Actief!.Fout.Should().Be("bestaat al");
    }

    [Fact]
    public async Task TijdensOpslaan_IsDeSessieBezig_EnWordtDubbelOpslaanGenegeerd()
    {
        var bewerking = new SpeeltijdBewerking();
        bewerking.StartBewerken(A);
        var (opslag, antwoord) = StartVertraagdeOpslag(bewerking);
        var aanroepen = 0;

        bewerking.Actief!.Bezig.Should().BeTrue();
        (await bewerking.OpslaanAsync((_, _) => { aanroepen++; return Task.FromResult(ApiResult<object>.Ok(new object())); }))
            .Should().BeFalse();
        aanroepen.Should().Be(0);

        antwoord.SetResult(ApiResult<object>.Fail("x"));
        await opslag;
        bewerking.Actief!.Bezig.Should().BeFalse();
    }

    [Fact]
    public void StartBewerken_WerktOpEenKopie()
    {
        var bewerking = new SpeeltijdBewerking();
        bewerking.StartBewerken(A);

        bewerking.Actief!.Model.WedstrijdTotaal = 99;

        A.WedstrijdTotaal.Should().Be(65);
        bewerking.IsInBewerking(A).Should().BeTrue();
        bewerking.IsInBewerking(B).Should().BeFalse();
    }

    [Fact]
    public void NieuweCategorie_StaatNietOnderEenBestaandeRegel()
    {
        var bewerking = new SpeeltijdBewerking();
        bewerking.StartNieuw();
        bewerking.Actief!.Model.Leeftijd = "JO10";

        bewerking.IsInBewerking(A).Should().BeFalse();
    }
}
