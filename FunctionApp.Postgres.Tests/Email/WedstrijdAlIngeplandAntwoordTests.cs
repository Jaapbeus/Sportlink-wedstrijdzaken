using AwesomeAssertions;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Planner;
using Planner.Shared;
using FunctionApp.Postgres.Processing;
using Newtonsoft.Json;
using Planner.Shared.Email.Trace;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Pariteit met de SQL Server-tier (#1568): <c>VerwerkMetPlannerAsync</c> geeft bij het opponent-pad
/// <c>wedstrijdAlIngepland</c> terug; <c>BouwTemplateAntwoord</c> moest daar het informatieve antwoord bij
/// maken. Zonder die tak viel het door naar het standaard-beschikbaarheidsantwoord. Geen database nodig.
/// </summary>
public class WedstrijdAlIngeplandAntwoordTests
{
    [Fact]
    public async Task WedstrijdAlIngepland_GeeftHetInformatieveAntwoord_EnGeenPlanbaarheidsuitkomst()
    {
        var json = JsonConvert.SerializeObject(new
        {
            wedstrijdAlIngepland = true,
            wedstrijd = new ZoekWedstrijdResponse { Wedstrijd = "Voorbeeld JO13-2 - Gast JO13-1", Datum = "2026-10-10", AanvangsTijd = "10:30" }
        });
        var trace = new TraceBuilder();

        var (_, body) = await BerichtPipeline.BouwTemplateAntwoord(
            new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck },
            json,
            new InkomendBericht { MessageId = "m", Afzender = "afzender@voorbeeld.test", AfzenderNaam = "Jan de Vries" },
            clubSettings: new ClubAppSettingsSnapshot("Planner", null, null, "Met vriendelijke groet", null),
            trace: trace);

        body.Should().Contain("staat al ingepland");
        body.Should().Contain("Voorbeeld JO13-2 - Gast JO13-1");
        trace.Stappen.Should().Contain(s => s.Code == TraceCodes.Sjabloon && s.Details["sjabloon"] == "wedstrijdAlIngepland");
    }
}
