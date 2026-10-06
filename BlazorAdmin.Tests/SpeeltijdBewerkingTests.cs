using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// #1552: een (vertraagd) opslagresultaat verandert uitsluitend de bewerksessie die het startte, en de
/// regel blijft geblokkeerd tot de lijst na de opslag opnieuw is opgehaald — ook als dat mislukt.
/// </summary>
public class SpeeltijdBewerkingTests
{
    private static SpeeltijdDto Regel(string leeftijd, int totaal) =>
        new() { Leeftijd = leeftijd, WedstrijdTotaal = totaal, WedstrijdHelft = 30, WedstrijdRust = 15, Veldafmeting = 1m };

    private static SpeeltijdDto A => Regel("JO10", 65);
    private static SpeeltijdDto B => Regel("JO11", 75);

    private static Task<ApiResult<List<SpeeltijdDto>>> Lijst(params SpeeltijdDto[] regels) =>
        Task.FromResult(ApiResult<List<SpeeltijdDto>>.Ok(regels.ToList()));

    private static Task<ApiResult<List<SpeeltijdDto>>> LijstFout(string melding) =>
        Task.FromResult(ApiResult<List<SpeeltijdDto>>.Fail(melding));

    private static Task<ApiResult<object>> Ok() => Task.FromResult(ApiResult<object>.Ok(new object()));
    private static Task<ApiResult<object>> Fout(string melding, int status = 500) => Task.FromResult(ApiResult<object>.Fail(melding, status));

    private static async Task<SpeeltijdBewerking> Geladen(params SpeeltijdDto[] regels)
    {
        var bewerking = new SpeeltijdBewerking();
        await bewerking.LaadAsync(() => Lijst(regels));
        return bewerking;
    }

    private static int Totaal(SpeeltijdBewerking bewerking, string leeftijd) =>
        bewerking.Items.Single(r => r.Leeftijd == leeftijd).WedstrijdTotaal;

    /// <summary>Opslag waarvan de PUT en de GET erna elk afzonderlijk worden vrijgegeven.</summary>
    private sealed class VertraagdeOpslag
    {
        public TaskCompletionSource<ApiResult<object>> Put { get; } = new();
        public TaskCompletionSource<ApiResult<List<SpeeltijdDto>>> Get { get; } = new();
        /// <summary>Wordt gezet zodra de opslag de lijst opnieuw opvraagt: de PUT is dan verwerkt.</summary>
        public TaskCompletionSource<bool> GetGestart { get; } = new();
        public List<SpeeltijdDto> Verzonden { get; } = new();
        public Task<bool> Resultaat { get; }

        public VertraagdeOpslag(SpeeltijdBewerking bewerking) =>
            Resultaat = bewerking.OpslaanAsync(
                (model, _) => { Verzonden.Add(Regel(model.Leeftijd, model.WedstrijdTotaal)); return Put.Task; },
                () => { GetGestart.TrySetResult(true); return Get.Task; });
    }

    [Fact]
    public async Task DirecteFout_StaatBijDeEigenSessie_EnVraagtDeLijstNietOpnieuwOp()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartBewerken(A);
        var lijstOpgevraagd = 0;

        var opgeslagen = await bewerking.OpslaanAsync((_, _) => Fout("ongeldig", 400), () => { lijstOpgevraagd++; return Lijst(A, B); });

        opgeslagen.Should().BeFalse();
        bewerking.Actief!.Fout.Should().Be("ongeldig");
        bewerking.Melding.Should().BeNull();
        lijstOpgevraagd.Should().Be(0);
    }

    [Fact]
    public async Task DirectSucces_SluitHetFormulier_EnToontDeVerseLijst()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartBewerken(A);
        bewerking.Actief!.Model.WedstrijdTotaal = 90;

        (await bewerking.OpslaanAsync((_, _) => Ok(), () => Lijst(Regel("JO10", 90), B))).Should().BeTrue();

        bewerking.Actief.Should().BeNull();
        Totaal(bewerking, "JO10").Should().Be(90);
        bewerking.IsOpslagBezig(A).Should().BeFalse();
    }

    [Fact]
    public async Task VertraagdeFout_NaWisselenNaarB_KomtNietBijB()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartBewerken(A);
        var opslag = new VertraagdeOpslag(bewerking);

        bewerking.StartBewerken(B);
        var sessieB = bewerking.Actief!;
        bewerking.IsOpslagBezig(A).Should().BeTrue();
        bewerking.IsOpslagBezig(B).Should().BeFalse();
        opslag.Put.SetResult(ApiResult<object>.Fail("serverfout"));
        (await opslag.Resultaat).Should().BeFalse();

        bewerking.Actief.Should().BeSameAs(sessieB);
        sessieB.Fout.Should().BeNull();
        bewerking.Melding.Should().Be("Opslaan van JO10 is mislukt: serverfout");
        bewerking.IsOpslagBezig(A).Should().BeFalse("ook een mislukte opslag geeft de regel vrij");
    }

    [Fact]
    public async Task VertraagdSucces_NaWisselenNaarB_SluitBNiet_EnVerverstDeLijst()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartBewerken(A);
        bewerking.Actief!.Model.WedstrijdTotaal = 90;
        var opslag = new VertraagdeOpslag(bewerking);

        bewerking.StartBewerken(B);
        var sessieB = bewerking.Actief!;
        opslag.Put.SetResult(ApiResult<object>.Ok(new object()));
        opslag.Get.SetResult(ApiResult<List<SpeeltijdDto>>.Ok(new() { Regel("JO10", 90), B }));

        (await opslag.Resultaat).Should().BeTrue();
        bewerking.Actief.Should().BeSameAs(sessieB);
        bewerking.Melding.Should().BeNull();
        Totaal(bewerking, "JO10").Should().Be(90);
    }

    [Fact]
    public async Task VertraagdResultaat_NaNieuweCategorie_RaaktHetNieuweFormulierNiet()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartBewerken(A);
        var opslag = new VertraagdeOpslag(bewerking);

        bewerking.StartNieuw();
        var nieuw = bewerking.Actief!;
        opslag.Put.SetResult(ApiResult<object>.Fail("serverfout"));
        await opslag.Resultaat;

        bewerking.Actief.Should().BeSameAs(nieuw);
        nieuw.IsNieuw.Should().BeTrue();
        nieuw.Fout.Should().BeNull();
        bewerking.Melding.Should().Contain("JO10");
    }

    [Fact]
    public async Task VertraagdeFout_NaAnnuleren_WordtEenSluitbareMelding()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartBewerken(A);
        var opslag = new VertraagdeOpslag(bewerking);

        bewerking.Annuleer();
        opslag.Put.SetResult(ApiResult<object>.Fail("serverfout"));
        await opslag.Resultaat;

        bewerking.Actief.Should().BeNull();
        bewerking.Melding.Should().Be("Opslaan van JO10 is mislukt: serverfout");
        bewerking.SluitMelding();
        bewerking.Melding.Should().BeNull();
    }

    /// <summary>
    /// De door Codex gereproduceerde race (PR #1557, ronde 1, P2): PUT geslaagd, GET nog onderweg,
    /// tussentijdse render, heropenen van dezelfde categorie, tweede opslag van een ander veld.
    /// </summary>
    [Fact]
    public async Task NaGeslaagdePut_BlijftDeRegelGeblokkeerdTotDeLijstIsVerverst_EnGaatDeOpgeslagenWaardeNietVerloren()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartBewerken(A);
        bewerking.Actief!.Model.WedstrijdTotaal = 90;
        var opslag = new VertraagdeOpslag(bewerking);
        bewerking.Annuleer();                                   // annuleren tijdens de PUT

        opslag.Put.SetResult(ApiResult<object>.Ok(new object()));
        await opslag.GetGestart.Task;                          // PUT verwerkt, GET hangt

        bewerking.IsOpslagBezig(A).Should().BeTrue("het slot geldt tot de lijst actueel is");
        Totaal(bewerking, "JO10").Should().Be(90, "de regel is al lokaal bijgewerkt");
        bewerking.StartNieuw();                                 // een tussentijdse render/actie
        bewerking.StartBewerken(A).Should().BeFalse("heropenen in het GET-venster zou de oude kopie opleveren");
        bewerking.Actief!.IsNieuw.Should().BeTrue();

        opslag.Get.SetResult(ApiResult<List<SpeeltijdDto>>.Ok(new() { Regel("JO10", 90), B }));
        (await opslag.Resultaat).Should().BeTrue();

        bewerking.IsOpslagBezig(A).Should().BeFalse();
        bewerking.StartBewerken(Regel("JO10", 90)).Should().BeTrue();
        bewerking.Actief!.Model.WedstrijdTotaal.Should().Be(90, "het heropende formulier toont de opgeslagen waarde");

        bewerking.Actief.Model.WedstrijdRust = 13;              // tweede opslag: alleen rust wijzigt
        var tweede = new VertraagdeOpslag(bewerking);
        tweede.Put.SetResult(ApiResult<object>.Ok(new object()));
        tweede.Get.SetResult(ApiResult<List<SpeeltijdDto>>.Ok(new() { Regel("JO10", 90), B }));
        await tweede.Resultaat;

        tweede.Verzonden.Single().WedstrijdTotaal.Should().Be(90, "de tweede opslag schrijft de eerder opgeslagen 90 niet terug naar 65");
    }

    [Fact]
    public async Task LokaleBijwerking_MeldtZichVoorDeVerversing_ZodatDePaginaKanRenderen()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartBewerken(A);
        bewerking.Actief!.Model.WedstrijdTotaal = 90;
        var totaalBijSignaal = new List<int>();
        bewerking.Gewijzigd += () => totaalBijSignaal.Add(Totaal(bewerking, "JO10"));
        var opslag = new VertraagdeOpslag(bewerking);

        opslag.Put.SetResult(ApiResult<object>.Ok(new object()));
        await opslag.GetGestart.Task;

        totaalBijSignaal.Should().Equal(90);
        opslag.Get.SetResult(ApiResult<List<SpeeltijdDto>>.Ok(new() { Regel("JO10", 90), B }));
        await opslag.Resultaat;
    }

    [Fact]
    public async Task MislukteVerversing_HoudtDeLokaalBijgewerkteRegel_GeeftDeRegelVrij_EnMeldtDeFout()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartBewerken(A);
        bewerking.Actief!.Model.WedstrijdTotaal = 90;

        (await bewerking.OpslaanAsync((_, _) => Ok(), () => LijstFout("netwerkfout"))).Should().BeTrue();

        bewerking.VerversFout.Should().Contain("JO10").And.Contain("netwerkfout");
        bewerking.Melding.Should().BeNull();
        Totaal(bewerking, "JO10").Should().Be(90, "zonder verse lijst blijven de opgeslagen waarden staan");
        Totaal(bewerking, "JO11").Should().Be(75);
        bewerking.IsOpslagBezig(A).Should().BeFalse("na een mislukte verversing blijft de regel niet eeuwig geblokkeerd");
        bewerking.StartBewerken(bewerking.Items.Single(r => r.Leeftijd == "JO10")).Should().BeTrue();
        bewerking.Actief!.Model.WedstrijdTotaal.Should().Be(90);

        bewerking.SluitVerversFout();
        bewerking.VerversFout.Should().BeNull();
    }

    [Fact]
    public async Task GeslaagdeVerversing_RuimtEenEerdereVerversFoutOp()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartBewerken(A);
        await bewerking.OpslaanAsync((_, _) => Ok(), () => LijstFout("netwerkfout"));
        bewerking.VerversFout.Should().NotBeNull();

        await bewerking.LaadAsync(() => Lijst(A, B));

        bewerking.VerversFout.Should().BeNull();
    }

    [Fact]
    public async Task NieuweCategorie_StaatNaOpslagDirectInDeLijst_OokAlsDeVerversingMislukt()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartNieuw();
        bewerking.Actief!.Model.Leeftijd = "JO99";
        bewerking.Actief.Model.WedstrijdTotaal = 50;

        (await bewerking.OpslaanAsync((_, _) => Ok(), () => LijstFout("netwerkfout"))).Should().BeTrue();

        bewerking.Items.Should().HaveCount(3);
        Totaal(bewerking, "JO99").Should().Be(50);
        bewerking.VerversFout.Should().Contain("JO99");
    }

    [Fact]
    public async Task HeropenenTijdensLopendePut_WordtGeweigerd_AndereRegelNiet()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartBewerken(A);
        var opslag = new VertraagdeOpslag(bewerking);
        bewerking.Annuleer();

        bewerking.StartBewerken(A).Should().BeFalse();
        bewerking.Actief.Should().BeNull();
        bewerking.StartBewerken(B).Should().BeTrue("een andere categorie wordt niet geraakt");

        opslag.Put.SetResult(ApiResult<object>.Ok(new object()));
        opslag.Get.SetResult(ApiResult<List<SpeeltijdDto>>.Ok(new() { A, B }));
        await opslag.Resultaat;

        bewerking.Actief!.Model.Leeftijd.Should().Be("JO11", "de sessie van B blijft open");
        bewerking.StartBewerken(A).Should().BeTrue();
    }

    [Fact]
    public async Task NieuweCategorieInOpslag_BlokkeertGeenBestaandeRegelMetDieNaam()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartNieuw();
        bewerking.Actief!.Model.Leeftijd = "JO10";
        var opslag = new VertraagdeOpslag(bewerking);

        bewerking.IsOpslagBezig(A).Should().BeFalse("een POST van een nieuwe categorie is geen opslag van de bestaande regel");

        opslag.Put.SetResult(ApiResult<object>.Fail("bestaat al", 409));
        await opslag.Resultaat;
        bewerking.Actief!.Fout.Should().Be("bestaat al");
    }

    [Fact]
    public async Task TijdensOpslaan_IsDeSessieBezig_EnWordtDubbelOpslaanGenegeerd()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartBewerken(A);
        var opslag = new VertraagdeOpslag(bewerking);
        var aanroepen = 0;

        bewerking.Actief!.Bezig.Should().BeTrue();
        (await bewerking.OpslaanAsync((_, _) => { aanroepen++; return Ok(); }, () => Lijst(A, B))).Should().BeFalse();
        aanroepen.Should().Be(0);

        opslag.Put.SetResult(ApiResult<object>.Fail("x"));
        await opslag.Resultaat;
        bewerking.Actief!.Bezig.Should().BeFalse();
    }

    [Fact]
    public async Task Bewerken_EnLokaalBijwerken_WerkenOpKopieen()
    {
        var bewerking = await Geladen(A, B);
        var regelA = bewerking.Items.Single(r => r.Leeftijd == "JO10");
        bewerking.StartBewerken(regelA);
        var sessie = bewerking.Actief!;
        sessie.Model.WedstrijdTotaal = 99;
        regelA.WedstrijdTotaal.Should().Be(65, "het formulier werkt op een kopie van de regel");

        await bewerking.OpslaanAsync((_, _) => Ok(), () => LijstFout("x"));
        sessie.Model.WedstrijdTotaal = 1;

        Totaal(bewerking, "JO10").Should().Be(99, "de lijst krijgt een kopie, niet het formuliermodel");
        bewerking.IsInBewerking(A).Should().BeFalse();
    }

    [Fact]
    public async Task NieuweCategorie_StaatNietOnderEenBestaandeRegel()
    {
        var bewerking = await Geladen(A, B);
        bewerking.StartNieuw();
        bewerking.Actief!.Model.Leeftijd = "JO10";

        bewerking.IsInBewerking(A).Should().BeFalse();
    }

    [Fact]
    public async Task LaadFout_LaatDeBestaandeLijstStaan_EnGeeftHetResultaatTerug()
    {
        var bewerking = await Geladen(A, B);

        var resultaat = await bewerking.LaadAsync(() => LijstFout("offline"));

        resultaat.Success.Should().BeFalse();
        resultaat.ErrorMessage.Should().Be("offline");
        bewerking.Items.Should().HaveCount(2);
    }
}
