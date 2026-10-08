using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>#1583 (Codex-review): alleen de nieuwste logaanvraag mag lijst, fout en laadstatus publiceren.</summary>
public class EmailLogLaderTests
{
    private static readonly DateTime Vandaag = new(2026, 10, 7);

    private static ApiResult<EmailLogResponse> Antwoord(params int[] ids)
        => ApiResult<EmailLogResponse>.Ok(new EmailLogResponse
        {
            Count = ids.Length,
            Items = ids.Select(i => new EmailLogDto { Id = i }).ToList()
        });

    /// <summary>Lader waarvan elke aanvraag pas klaar komt als de test dat zegt.</summary>
    private sealed class Gestuurd
    {
        public readonly List<(string? Status, TaskCompletionSource<ApiResult<EmailLogResponse>> Klaar)> Aanvragen = new();
        public EmailLogLader Lader { get; }

        public Gestuurd() => Lader = new EmailLogLader((_, status, _) =>
        {
            var tcs = new TaskCompletionSource<ApiResult<EmailLogResponse>>();
            Aanvragen.Add((status, tcs));
            return tcs.Task;
        });
    }

    [Fact]
    public async Task OmgekeerdeCompletionvolgorde_ToontDeNieuwsteKeuze()
    {
        var g = new Gestuurd();

        var a = g.Lader.LaadAsync("Fout", "7d", Vandaag);          // A gestart
        var b = g.Lader.LaadAsync("Review", "7d", Vandaag);        // B gekozen
        g.Aanvragen[1].Klaar.SetResult(Antwoord(2));               // B komt eerst terug
        await b;
        g.Lader.Log!.Items.Select(i => i.Id).Should().Equal(2);
        g.Lader.Bezig.Should().BeFalse();

        g.Aanvragen[0].Klaar.SetResult(Antwoord(1));               // daarna A
        await a;

        g.Lader.Log!.Items.Select(i => i.Id).Should().Equal(new[] { 2 }); // de lijst blijft bij de nieuwste keuze (B) horen
        g.Lader.Bezig.Should().BeFalse();
        g.Lader.Fout.Should().BeNull();
    }

    [Fact]
    public async Task VerouderdeAanvraag_ZetDeLaadstatusNietUit_ZolangDeNieuwsteLoopt()
    {
        var g = new Gestuurd();

        var a = g.Lader.LaadAsync("Fout", "7d", Vandaag);
        var b = g.Lader.LaadAsync("Review", "7d", Vandaag);
        g.Aanvragen[0].Klaar.SetResult(Antwoord(1));               // A komt eerst terug, B loopt nog
        await a;

        g.Lader.Bezig.Should().BeTrue();
        g.Lader.Log.Should().BeNull("een verouderd resultaat mag niets publiceren");

        g.Aanvragen[1].Klaar.SetResult(Antwoord(2));
        await b;
        g.Lader.Log!.Items.Single().Id.Should().Be(2);
        g.Lader.Bezig.Should().BeFalse();
    }

    [Fact]
    public async Task VerouderdeFout_OverschrijftDeNieuwsteLijstNiet()
    {
        var g = new Gestuurd();

        var a = g.Lader.LaadAsync("Fout", "7d", Vandaag);
        var b = g.Lader.LaadAsync("Review", "7d", Vandaag);
        g.Aanvragen[1].Klaar.SetResult(Antwoord(2));
        await b;
        g.Aanvragen[0].Klaar.SetResult(ApiResult<EmailLogResponse>.Fail("Time-out"));
        await a;

        g.Lader.Fout.Should().BeNull();
        g.Lader.Log!.Items.Single().Id.Should().Be(2);
    }

    [Fact]
    public async Task VerouderdeUitzondering_OverschrijftDeNieuwsteLijstNiet()
    {
        var g = new Gestuurd();

        var a = g.Lader.LaadAsync("Fout", "7d", Vandaag);
        var b = g.Lader.LaadAsync("Review", "7d", Vandaag);
        g.Aanvragen[1].Klaar.SetResult(Antwoord(2));
        await b;
        g.Aanvragen[0].Klaar.SetException(new InvalidOperationException("netwerk"));
        await a;

        g.Lader.Fout.Should().BeNull();
        g.Lader.Log!.Items.Single().Id.Should().Be(2);
    }

    [Fact]
    public async Task Clubwissel_LeegtDeLijstEnNegeertDeLopendeAanvraagVanDeVorigeClub()
    {
        var g = new Gestuurd();

        var eersteClub = g.Lader.LaadAsync("", "7d", Vandaag);
        g.Aanvragen[0].Klaar.SetResult(Antwoord(10));
        await eersteClub;
        g.Lader.Log!.Items.Single().Id.Should().Be(10);

        var traag = g.Lader.LaadAsync("", "7d", Vandaag);                      // nog onderweg
        var nieuweClub = g.Lader.LaadAsync("", "7d", Vandaag, wisHuidige: true); // clubwissel
        g.Lader.Log.Should().BeNull("de lijst van de vorige club mag niet blijven staan");
        g.Lader.Bezig.Should().BeTrue();

        g.Aanvragen[2].Klaar.SetResult(Antwoord(20));
        await nieuweClub;
        g.Aanvragen[1].Klaar.SetResult(Antwoord(11));                          // oude club komt laat terug
        await traag;

        g.Lader.Log!.Items.Single().Id.Should().Be(20);
        g.Lader.Bezig.Should().BeFalse();
    }

    [Fact]
    public async Task EnkeleAanvraag_PubliceertResultaat_EnFoutenSlagenOp()
    {
        var g = new Gestuurd();

        var ok = g.Lader.LaadAsync("Review", "alles", Vandaag);
        g.Lader.Bezig.Should().BeTrue();
        g.Aanvragen[0].Klaar.SetResult(Antwoord(1, 2));
        await ok;
        g.Lader.Log!.Items.Should().HaveCount(2);

        var fout = g.Lader.LaadAsync("Review", "alles", Vandaag);
        g.Aanvragen[1].Klaar.SetResult(ApiResult<EmailLogResponse>.Fail("Geen toegang", 403));
        await fout;
        g.Lader.Log.Should().BeNull();
        g.Lader.Fout.Should().Be("Geen toegang");
        g.Lader.Bezig.Should().BeFalse();
    }

    [Fact]
    public async Task Filters_GaanVertaaldNaarDeApi()
    {
        DateTime? vanaf = null; string? status = null; var limiet = 0;
        var lader = new EmailLogLader((v, s, l) => { vanaf = v; status = s; limiet = l; return Task.FromResult(Antwoord()); });

        await lader.LaadAsync("Review", "7d", Vandaag);

        (vanaf, status, limiet).Should().Be((new DateTime(2026, 9, 30), "Review", EmailLogFilter.MaxRegels));
    }
}
