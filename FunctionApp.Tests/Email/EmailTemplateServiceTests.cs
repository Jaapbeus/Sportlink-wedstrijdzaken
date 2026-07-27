using FluentAssertions;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Regressietests voor issue #706 (restant van #677): e-mailtemplates moeten van de club komen
/// waarvoor het bericht verwerkt wordt.
///
/// Twee samenhangende fouten lagen hieronder:
///   1. <c>GetTemplateAsync</c> las de ClubCode uit de proces-globale AppSettings-cache, die altijd
///      de primaire (SyncEnabled) club van de deployment bevat. Een dry-run met de democlub
///      geselecteerd kreeg daardoor de templates van de productieclub.
///   2. De statische template-cache was gesleuteld op alléén de template-key. Zodra er een
///      clubCode-parameter bij komt, is dat een data-isolatiefout: de eerste club die een template
///      ophaalt vult de cache en elke volgende club krijgt diezelfde rij terug.
///
/// Zonder live SQL-database is de DB-tak van <c>GetTemplateAsync</c> in dit testproces onbereikbaar
/// (geen connectiestring → exception → terugval op <c>null</c>). Dat is hier juist bruikbaar: de
/// cache-tak zit vóór de database, dus een gecachte template die tóch bij een andere club opduikt
/// is aantoonbaar een cachelek en niet een DB-treffer.
/// </summary>
public class EmailTemplateServiceTests
{
    // Neutrale placeholders: nooit de naam of code van een echte club in tests.
    private const string PrimaireClub = "TESTCLUB-PRIMAIR";
    private const string DemoClub = "ALLSTARS";

    private static EmailTemplate MaakTemplate(string key, string club)
        => new(key, $"Onderwerp {club}", $"Body van {club}");

    [Fact]
    public void TryGetCached_TemplateVanEenAndereClub_GeeftGeenTreffer()
    {
        var key = "bevestiging_" + Guid.NewGuid().ToString("N");
        EmailTemplateService.StoreInCache(key, PrimaireClub, MaakTemplate(key, PrimaireClub));

        var treffer = EmailTemplateService.TryGetCached(key, DemoClub, out var template);

        treffer.Should().BeFalse("de cache is gesleuteld op (club, key) — clubs mogen elkaars templates niet zien");
        template.Should().BeNull();
    }

    [Fact]
    public void TryGetCached_ElkeClubKrijgtHaarEigenTemplate()
    {
        var key = "beschikbaarheid_check_" + Guid.NewGuid().ToString("N");
        EmailTemplateService.StoreInCache(key, PrimaireClub, MaakTemplate(key, PrimaireClub));
        EmailTemplateService.StoreInCache(key, DemoClub, MaakTemplate(key, DemoClub));

        EmailTemplateService.TryGetCached(key, PrimaireClub, out var primair).Should().BeTrue();
        EmailTemplateService.TryGetCached(key, DemoClub, out var demo).Should().BeTrue();

        primair!.Body.Should().Be($"Body van {PrimaireClub}");
        demo!.Body.Should().Be($"Body van {DemoClub}");
    }

    [Fact]
    public void InvalidateCache_VerwijdertOokDeClubGesleuteldeEntries()
    {
        // De Admin GUI (PUT/reset op /api/beheer/templates) leunt hierop: na het opslaan van een
        // template moet de volgende verwerking de nieuwe versie ophalen.
        var key = "buiten_scope_" + Guid.NewGuid().ToString("N");
        EmailTemplateService.StoreInCache(key, PrimaireClub, MaakTemplate(key, PrimaireClub));
        EmailTemplateService.StoreInCache(key, DemoClub, MaakTemplate(key, DemoClub));

        EmailTemplateService.InvalidateCache();

        EmailTemplateService.TryGetCached(key, PrimaireClub, out _).Should().BeFalse();
        EmailTemplateService.TryGetCached(key, DemoClub, out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetTemplateAsync_MetEigenClubCode_GeeftDeGecachteTemplate()
    {
        var key = "herplan_verzoek_" + Guid.NewGuid().ToString("N");
        EmailTemplateService.StoreInCache(key, DemoClub, MaakTemplate(key, DemoClub));

        var template = await EmailTemplateService.GetTemplateAsync(key, DemoClub);

        template.Should().NotBeNull();
        template!.Body.Should().Be($"Body van {DemoClub}");
    }

    [Fact]
    public async Task GetTemplateAsync_AndereClub_KrijgtNietDeTemplateVanDeEersteClub()
    {
        // Dit is exact het faalscenario van #706: eerst warmt club A de cache, daarna vraagt club B
        // dezelfde key op. Met de oude key-only cache kreeg B de template van A terug.
        var key = "team_contact_opvragen_" + Guid.NewGuid().ToString("N");
        EmailTemplateService.StoreInCache(key, PrimaireClub, MaakTemplate(key, PrimaireClub));

        var template = await EmailTemplateService.GetTemplateAsync(key, DemoClub);

        template.Should().BeNull("zonder eigen rij voor deze club hoort de caller op de hardcoded default terug te vallen");
    }

    [Fact]
    public async Task GetTemplateAsync_ZonderClubCode_ValtTerugOpDePrimaireClub_EnNooitOpEenAndereClub()
    {
        // clubCode = null is het pad van de echte e-mailpipeline: die resolveert via
        // AppSettings.RequireClubCode naar de primaire club. In dit testproces is dbo.AppSettings
        // nooit geladen, dus die resolutie faalt luid — en het resultaat is een terugval op de
        // hardcoded default, niet de template van de club die de cache vulde.
        var key = "bevestiging_" + Guid.NewGuid().ToString("N");
        EmailTemplateService.StoreInCache(key, DemoClub, MaakTemplate(key, DemoClub));

        var template = await EmailTemplateService.GetTemplateAsync(key);

        template.Should().BeNull();
    }

    [Fact]
    public void RequireClubCode_ZonderExpliciteWaarde_FaaltAlsDePrimaireClubOntbreekt()
    {
        // Kanarie voor de resolutie die GetTemplateAsync gebruikt: een ontbrekende clubCode mag
        // nooit stil een lege string worden — dat zou het ClubCode-filter in de query uitschakelen.
        var act = () => SportlinkFunction.SystemUtilities.AppSettings.RequireClubCode(null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*clubCode*");
    }

    [Fact]
    public async Task GetTemplateAsync_LegeKey_GeeftNullZonderClubResolutie()
    {
        var template = await EmailTemplateService.GetTemplateAsync("   ", DemoClub);

        template.Should().BeNull();
    }
}
