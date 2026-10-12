using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using SportlinkFunction.Planner;

namespace SportlinkFunction.Email;

/// <summary>
/// Tier-adapter voor <see cref="TemplateAntwoordBouwer"/> (#1627): vertaalt de takkeuze uit Planner.Shared
/// naar de <see cref="BerichtResponseGenerator"/>-aanroepen en de DTO's van deze tier. Bevat geen
/// beslislogica; die (en de volgorde van de takken) staat in Planner.Shared.
/// </summary>
internal sealed class TemplateAntwoordBron : ITemplateAntwoordBron
{
    private readonly BerichtClassificatie _classificatie;
    private readonly InkomendBericht _bericht;
    private readonly ClubAppSettingsSnapshot? _clubSettings;
    private readonly TraceBuilder? _trace;
    private readonly Func<string, Task<EmailTemplate?>> _haalSjabloonOp;

    public TemplateAntwoordBron(
        BerichtClassificatie classificatie,
        InkomendBericht bericht,
        ClubAppSettingsSnapshot? clubSettings,
        TraceBuilder? trace,
        Func<string, Task<EmailTemplate?>> haalSjabloonOp)
    {
        _classificatie = classificatie;
        _bericht = bericht;
        _clubSettings = clubSettings;
        _trace = trace;
        _haalSjabloonOp = haalSjabloonOp;
    }

    public void ValideerWedstrijd(JToken? wedstrijd) => wedstrijd?.ToObject<ZoekWedstrijdResponse>();

    public (string onderwerp, string body) WedstrijdAlIngepland(JToken? wedstrijd)
        => BerichtResponseGenerator.BouwWedstrijdAlIngeplandAntwoord(
            wedstrijd?.ToObject<ZoekWedstrijdResponse>(), _classificatie, _bericht, _clubSettings);

    public (string onderwerp, string body) TeamOnbekend(string? tegenstander)
        => BerichtResponseGenerator.BouwTeamOnbekendAntwoord(
            tegenstander ?? _classificatie.Tegenstander ?? "", _classificatie, _bericht, _clubSettings);

    public (string onderwerp, string body) DatumOnbekend()
    {
        var datumOnbekendTemplate = new EmailTemplate(
            "datum_onbekend", "",
            "{{aanhef}} {{voornaam}},\n\n"
            + "Bedankt voor je bericht. We konden er geen concrete datum uit opmaken. "
            + "Kun je aangeven welke datum of datums je in gedachten hebt? "
            + "Dan controleren we de beschikbaarheid van onze velden voor je.");
        return BerichtResponseGenerator.BouwAangepasteAntwoord(
            datumOnbekendTemplate, _classificatie, _bericht, _clubSettings);
    }

    public (string onderwerp, string body) MultiDatum(IReadOnlyList<(string datum, JToken? response)> resultaten)
        => BerichtResponseGenerator.BouwMultiDatumBeschikbaarheidAntwoord(
            resultaten.Select(r => (r.datum, r.response?.ToObject<CheckAvailabilityResponse>() ?? new CheckAvailabilityResponse())).ToList(),
            _classificatie, _bericht, _clubSettings);

    public async Task<(string onderwerp, string body)> BeschikbaarheidStandaardAsync(string plannerResponseJson)
    {
        var checkResponse = JsonConvert.DeserializeObject<CheckAvailabilityResponse>(plannerResponseJson);
        var template = await _haalSjabloonOp("beschikbaarheid_check");
        if (template != null)
            return _trace.MeldOverride("beschikbaarheid_check", BerichtResponseGenerator.BouwAangepasteAntwoord(
                template, _classificatie, _bericht, _clubSettings, gecontroleerdDagdeel: checkResponse?.GecontroleerdDagdeel));

        return BerichtResponseGenerator.BouwBeschikbaarheidAntwoord(
            checkResponse ?? new CheckAvailabilityResponse(), _classificatie, _bericht, _clubSettings);
    }

    public (string onderwerp, string body) HerplanTeLaat(JToken? wedstrijd, int deadlineDagen, int dagenTotWedstrijd)
        => BerichtResponseGenerator.BouwHerplanTeLaatAntwoord(
            wedstrijd?.ToObject<ZoekWedstrijdResponse>(), deadlineDagen, dagenTotWedstrijd, _classificatie, _bericht, _clubSettings);

    public (string onderwerp, string body) VerzetZonderDatum(JToken? wedstrijd, List<string> vrijeZaterdagen)
        => BerichtResponseGenerator.BouwVerzetZonderDatumAntwoord(
            wedstrijd?.ToObject<ZoekWedstrijdResponse>(), vrijeZaterdagen, _classificatie, _bericht, _clubSettings);

    public (string onderwerp, string body) HerplanGewensteDatum(JToken? wedstrijd, string? gewensteDatum, JToken? beschikbaarheid)
        => BerichtResponseGenerator.BouwHerplanGewensteDatumAntwoord(
            wedstrijd?.ToObject<ZoekWedstrijdResponse>(), gewensteDatum,
            beschikbaarheid?.ToObject<CheckAvailabilityResponse>(), _classificatie, _bericht, _clubSettings);

    public (string onderwerp, string body) HerplanStandaard(JToken? wedstrijd, JToken? herplanOpties)
        => BerichtResponseGenerator.BouwHerplanAntwoord(
            wedstrijd?.ToObject<ZoekWedstrijdResponse>(), herplanOpties?.ToObject<HerplanCheckResponse>(),
            _classificatie, _bericht, _clubSettings);

    public async Task<(string onderwerp, string body)?> ProbeerSjabloonAsync(string sleutel)
    {
        var template = await _haalSjabloonOp(sleutel);
        if (template == null) return null;
        return _trace.MeldOverride(sleutel,
            BerichtResponseGenerator.BouwAangepasteAntwoord(template, _classificatie, _bericht, _clubSettings));
    }

    public (string onderwerp, string body) TeamContactStandaard()
        => BerichtResponseGenerator.BouwTeamContactAutoReply(_classificatie, _bericht, _clubSettings);

    public (string onderwerp, string body) BevestigingStandaard()
        => BerichtResponseGenerator.BouwBevestigingAntwoord(_bericht, _classificatie, _clubSettings);

    public (string onderwerp, string body) BuitenScopeStandaard()
        => BerichtResponseGenerator.BouwBuitenScopeAntwoord(_bericht, _clubSettings);
}
