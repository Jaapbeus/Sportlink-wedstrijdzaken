#if PLANNER_DTO_POSTGRES
using Dto = FunctionApp.Postgres.Planner;
#else
using Dto = SportlinkFunction.Planner;
#endif
using System.Reflection;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// Characterization-tests voor de planner-wire-DTO's die op beide databasetiers uit één gedeelde
/// bron komen (<c>GedeeldeBron/PlannerDto/</c>, #1856). Dit bestand wordt link-gecompileerd in
/// <c>FunctionApp.Tests</c> én <c>FunctionApp.Postgres.Tests</c>: één golden, twee compilaties.
///
/// Wat hier vastligt, en dus niet ongemerkt kan veranderen: de naamruimte per tier (publiek
/// contract), de lijst publieke eigenschappen met type en nullability, de geïmplementeerde
/// interfaces, en de geserialiseerde JSON van een standaardinstantie (System.Text.Json, standaard
/// én web-opties). De golden is gemeten op de situatie vóór de verhuizing (twee losse kopieën) en
/// bleef daarna byte-voor-byte gelijk.
/// </summary>
public class PlannerDtoVormTests
{
#if PLANNER_DTO_POSTGRES
    private const string VerwachteNaamruimte = "FunctionApp.Postgres.Planner";
#else
    private const string VerwachteNaamruimte = "SportlinkFunction.Planner";
#endif

    private static readonly Type[] GedeeldeTypen =
    [
        typeof(Dto.CheckAvailabilityRequest), typeof(Dto.BevestigRequest),
        typeof(Dto.CheckAvailabilityResponse), typeof(Dto.TeamConflictInfo),
        typeof(Dto.BeschikbaarVenster), typeof(Dto.DoordeweeksBeschikbaarRequest),
        typeof(Dto.DoordeweeksBeschikbaarResponse), typeof(Dto.DoordeweekseDatum),
        typeof(Dto.BestaandeWedstrijdSamenvatting), typeof(Dto.ZoekWedstrijdRequest),
        typeof(Dto.ZoekWedstrijdResponse), typeof(Dto.HerplanCheckRequest),
        typeof(Dto.HerplanCheckResponse), typeof(Dto.HerplanBevestigRequest),
        typeof(Dto.HerplanBevestigResponse), typeof(Dto.TeamScheduleWedstrijd),
        typeof(Dto.TeamScheduleZaterdag), typeof(Dto.TeamScheduleResponse),
        typeof(Dto.AutoPlanRequest), typeof(Dto.AutoPlanWedstrijdItem),
        typeof(Dto.AutoPlanResponse), typeof(Dto.AutoPlanToepassenRequest),
        typeof(Dto.AutoPlanToepassenResponse),
    ];

    [Fact]
    public void GedeeldeTypen_StaanInDeNaamruimteVanDeEigenTier()
    {
        GedeeldeTypen.Should().HaveCount(23);
        GedeeldeTypen.Select(t => t.Namespace).Distinct().Should().Equal(VerwachteNaamruimte);
    }

    [Fact]
    public void GedeeldeTypen_HebbenDezelfdeVormEnJsonAlsVoorDeVerhuizing()
    {
        var gemeten = string.Join('\n', GedeeldeTypen.Select(Beschrijf));

        var dump = Environment.GetEnvironmentVariable("PLANNER_DTO_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            File.WriteAllText(dump, gemeten + "\n");
        }

        gemeten.Should().Be(Golden.Trim());
    }

    private static string Beschrijf(Type type)
    {
        var nullability = new NullabilityInfoContext();
        var eigenschappen = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => $"{p.Name}:{TypeNaam(p.PropertyType)}{(nullability.Create(p).ReadState == NullabilityState.Nullable ? "?" : "")}");
        var interfaces = type.GetInterfaces().Select(i => i.FullName).OrderBy(n => n, StringComparer.Ordinal);
        var instantie = Activator.CreateInstance(type);
        var standaard = JsonSerializer.Serialize(instantie, type);
        var web = JsonSerializer.Serialize(instantie, type, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var sb = new StringBuilder();
        sb.Append(type.Name).Append(" | ").Append(string.Join(',', eigenschappen))
          .Append(" | ").Append(string.Join(',', interfaces))
          .Append(" | ").Append(standaard).Append(" | ").Append(web);
        return sb.ToString();
    }

    private static string TypeNaam(Type t) =>
        t.IsGenericType
            ? $"{t.Name.Split('`')[0]}<{string.Join(',', t.GetGenericArguments().Select(TypeNaam))}>"
            : Nullable.GetUnderlyingType(t) is { } onderliggend ? onderliggend.Name : t.Name;

    private const string Golden = """
CheckAvailabilityRequest | Datum:String,AanvangsTijd:String?,Dagdeel:String?,LeeftijdsCategorie:String?,TeamNaam:String?,Tegenstander:String?,WedstrijdDuurMinuten:Nullable<Int32>?,HeelVeld:Nullable<Boolean>? |  | {"Datum":"","AanvangsTijd":null,"Dagdeel":null,"LeeftijdsCategorie":null,"TeamNaam":null,"Tegenstander":null,"WedstrijdDuurMinuten":null,"HeelVeld":null} | {"datum":"","aanvangsTijd":null,"dagdeel":null,"leeftijdsCategorie":null,"teamNaam":null,"tegenstander":null,"wedstrijdDuurMinuten":null,"heelVeld":null}
BevestigRequest | Datum:String,AanvangsTijd:String,VeldNummer:Int32,LeeftijdsCategorie:String?,TeamNaam:String?,Tegenstander:String?,AangevraagdDoor:String?,WedstrijdDuurMinuten:Nullable<Int32>?,HeelVeld:Nullable<Boolean>? | Planner.Shared.Autorisatie.IBevestigAanvragerRequest,Planner.Shared.Autorisatie.IPlannerAanvragerRequest | {"Datum":"","AanvangsTijd":"","VeldNummer":0,"LeeftijdsCategorie":null,"TeamNaam":null,"Tegenstander":null,"AangevraagdDoor":null,"WedstrijdDuurMinuten":null,"HeelVeld":null} | {"datum":"","aanvangsTijd":"","veldNummer":0,"leeftijdsCategorie":null,"teamNaam":null,"tegenstander":null,"aangevraagdDoor":null,"wedstrijdDuurMinuten":null,"heelVeld":null}
CheckAvailabilityResponse | Beschikbaar:Boolean,Toewijzing:SlotToewijzing?,TeamConflict:TeamConflictInfo?,Reden:String?,GecontroleerdDagdeel:String?,Alternatieven:List<SlotToewijzing>,BeschikbareVensters:List<BeschikbaarVenster>?,Waarschuwingen:List<String> | Planner.Shared.Email.IAntwoordBeschikbaarheid | {"Beschikbaar":false,"Toewijzing":null,"TeamConflict":null,"Reden":null,"GecontroleerdDagdeel":null,"Alternatieven":[],"BeschikbareVensters":null,"Waarschuwingen":[]} | {"beschikbaar":false,"toewijzing":null,"teamConflict":null,"reden":null,"gecontroleerdDagdeel":null,"alternatieven":[],"beschikbareVensters":null,"waarschuwingen":[]}
TeamConflictInfo | Wedstrijd:String,AanvangsTijd:String,EindTijd:String,VeldNaam:String |  | {"Wedstrijd":"","AanvangsTijd":"","EindTijd":"","VeldNaam":""} | {"wedstrijd":"","aanvangsTijd":"","eindTijd":"","veldNaam":""}
BeschikbaarVenster | VeldNummer:Int32,VeldNaam:String,Van:String,Tot:String,MaxDuurMinuten:Int32,Opmerking:String?,VeldType:String? | Planner.Shared.Email.IAntwoordVenster | {"VeldNummer":0,"VeldNaam":"","Van":"","Tot":"","MaxDuurMinuten":0,"Opmerking":null,"VeldType":null} | {"veldNummer":0,"veldNaam":"","van":"","tot":"","maxDuurMinuten":0,"opmerking":null,"veldType":null}
DoordeweeksBeschikbaarRequest | DagFilter:String?,DuurMinuten:Nullable<Int32>?,LeeftijdsCategorie:String? |  | {"DagFilter":null,"DuurMinuten":null,"LeeftijdsCategorie":null} | {"dagFilter":null,"duurMinuten":null,"leeftijdsCategorie":null}
DoordeweeksBeschikbaarResponse | BeschikbareDatums:List<DoordeweekseDatum>,DagFilter:String?,SeizoenEinde:String,AantalBeschikbaar:Int32 |  | {"BeschikbareDatums":[],"DagFilter":null,"SeizoenEinde":"","AantalBeschikbaar":0} | {"beschikbareDatums":[],"dagFilter":null,"seizoenEinde":"","aantalBeschikbaar":0}
DoordeweekseDatum | Datum:String,DagVanWeek:String,BeschikbaarVan:String,BeschikbaarTot:String,Zonsondergang:String,MaxDuurMinuten:Int32,PastGewensteDuur:Boolean,GeplandeWedstrijden:List<BestaandeWedstrijdSamenvatting> |  | {"Datum":"","DagVanWeek":"","BeschikbaarVan":"","BeschikbaarTot":"","Zonsondergang":"","MaxDuurMinuten":0,"PastGewensteDuur":false,"GeplandeWedstrijden":[]} | {"datum":"","dagVanWeek":"","beschikbaarVan":"","beschikbaarTot":"","zonsondergang":"","maxDuurMinuten":0,"pastGewensteDuur":false,"geplandeWedstrijden":[]}
BestaandeWedstrijdSamenvatting | Wedstrijd:String,AanvangsTijd:String,EindTijd:String |  | {"Wedstrijd":"","AanvangsTijd":"","EindTijd":""} | {"wedstrijd":"","aanvangsTijd":"","eindTijd":""}
ZoekWedstrijdRequest | TeamNaam:String,Datum:String |  | {"TeamNaam":"","Datum":""} | {"teamNaam":"","datum":""}
ZoekWedstrijdResponse | Wedstrijdcode:Int64,Wedstrijd:String,Datum:String,AanvangsTijd:String,EindTijd:String,VeldNaam:String?,LeeftijdsCategorie:String?,DuurMinuten:Int32,VeldDeelGebruik:Decimal | Planner.Shared.Email.IAntwoordWedstrijd | {"Wedstrijdcode":0,"Wedstrijd":"","Datum":"","AanvangsTijd":"","EindTijd":"","VeldNaam":null,"LeeftijdsCategorie":null,"DuurMinuten":0,"VeldDeelGebruik":0} | {"wedstrijdcode":0,"wedstrijd":"","datum":"","aanvangsTijd":"","eindTijd":"","veldNaam":null,"leeftijdsCategorie":null,"duurMinuten":0,"veldDeelGebruik":0}
HerplanCheckRequest | Wedstrijdcode:Int64,VoorkeurTijd:String?,Dagdeel:String?,Richting:String? |  | {"Wedstrijdcode":0,"VoorkeurTijd":null,"Dagdeel":null,"Richting":null} | {"wedstrijdcode":0,"voorkeurTijd":null,"dagdeel":null,"richting":null}
HerplanCheckResponse | HuidigeWedstrijd:ZoekWedstrijdResponse,Beschikbaar:Boolean,Alternatieven:List<SlotToewijzing>,Reden:String?,Waarschuwingen:List<String> | Planner.Shared.Email.IAntwoordHerplanOpties | {"HuidigeWedstrijd":{"Wedstrijdcode":0,"Wedstrijd":"","Datum":"","AanvangsTijd":"","EindTijd":"","VeldNaam":null,"LeeftijdsCategorie":null,"DuurMinuten":0,"VeldDeelGebruik":0},"Beschikbaar":false,"Alternatieven":[],"Reden":null,"Waarschuwingen":[]} | {"huidigeWedstrijd":{"wedstrijdcode":0,"wedstrijd":"","datum":"","aanvangsTijd":"","eindTijd":"","veldNaam":null,"leeftijdsCategorie":null,"duurMinuten":0,"veldDeelGebruik":0},"beschikbaar":false,"alternatieven":[],"reden":null,"waarschuwingen":[]}
HerplanBevestigRequest | Wedstrijdcode:Int64,GewensteAanvangsTijd:String,GewenstVeldNummer:Nullable<Int32>?,AangevraagdDoor:String?,Opmerking:String? | Planner.Shared.Autorisatie.IHerplanAanvragerRequest,Planner.Shared.Autorisatie.IPlannerAanvragerRequest | {"Wedstrijdcode":0,"GewensteAanvangsTijd":"","GewenstVeldNummer":null,"AangevraagdDoor":null,"Opmerking":null} | {"wedstrijdcode":0,"gewensteAanvangsTijd":"","gewenstVeldNummer":null,"aangevraagdDoor":null,"opmerking":null}
HerplanBevestigResponse | Id:Int32,Wedstrijdcode:Int64,HuidigeWedstrijd:String,GewensteAanvangsTijd:String,GewenstVeldNummer:Nullable<Int32>?,Status:String |  | {"Id":0,"Wedstrijdcode":0,"HuidigeWedstrijd":"","GewensteAanvangsTijd":"","GewenstVeldNummer":null,"Status":""} | {"id":0,"wedstrijdcode":0,"huidigeWedstrijd":"","gewensteAanvangsTijd":"","gewenstVeldNummer":null,"status":""}
TeamScheduleWedstrijd | Datum:String,AanvangsTijd:String,ThuisUit:String,Tegenstander:String,Type:String,Veld:String?,Wedstrijdcode:Nullable<Int64>? |  | {"Datum":"","AanvangsTijd":"","ThuisUit":"","Tegenstander":"","Type":"","Veld":null,"Wedstrijdcode":null} | {"datum":"","aanvangsTijd":"","thuisUit":"","tegenstander":"","type":"","veld":null,"wedstrijdcode":null}
TeamScheduleZaterdag | Datum:String,Status:String,BezetDoor:TeamScheduleWedstrijd? |  | {"Datum":"","Status":"","BezetDoor":null} | {"datum":"","status":"","bezetDoor":null}
TeamScheduleResponse | Team:String,SeizoenEinde:String,Zaterdagen:List<TeamScheduleZaterdag>,Wedstrijden:List<TeamScheduleWedstrijd> |  | {"Team":"","SeizoenEinde":"","Zaterdagen":[],"Wedstrijden":[]} | {"team":"","seizoenEinde":"","zaterdagen":[],"wedstrijden":[]}
AutoPlanRequest | Datum:String,BufferMinuten:Nullable<Int32>? |  | {"Datum":"","BufferMinuten":null} | {"datum":"","bufferMinuten":null}
AutoPlanWedstrijdItem | WedstrijdCode:Nullable<Int64>?,Wedstrijd:String,TeamNaam:String,LeeftijdsCategorie:String?,Competitiesoort:String?,DuurMinuten:Int32,Veldafmeting:Decimal,HuidigeVeld:String?,HuidigeTijd:String?,HeeftVeld:Boolean,HeeftTijd:Boolean,OptimaalVeldNummer:Nullable<Int32>?,OptimaalVeldNaam:String?,OptimaalVeld:String?,OptimaalTijd:String?,Status:String,NietInplanbaaarReden:String?,VoorkeurTijd:String?,VoorkeurAfwijkingMinuten:Nullable<Int32>?,VoorkeurBron:String?,VoorkeurStatus:String,VoorkeurVeldNummer:Nullable<Int32>?,VoorkeurVeldToegepast:Nullable<Boolean>?,TeamBufferVoor:Nullable<Int32>?,TeamBufferNa:Nullable<Int32>? | Planner.Shared.Deel.IPlanWedstrijdRegel | {"WedstrijdCode":null,"Wedstrijd":"","TeamNaam":"","LeeftijdsCategorie":null,"Competitiesoort":null,"DuurMinuten":0,"Veldafmeting":0,"HuidigeVeld":null,"HuidigeTijd":null,"HeeftVeld":false,"HeeftTijd":false,"OptimaalVeldNummer":null,"OptimaalVeldNaam":null,"OptimaalVeld":null,"OptimaalTijd":null,"Status":"ongewijzigd","NietInplanbaaarReden":null,"VoorkeurTijd":null,"VoorkeurAfwijkingMinuten":null,"VoorkeurBron":null,"VoorkeurStatus":"geen-voorkeur","VoorkeurVeldNummer":null,"VoorkeurVeldToegepast":null,"TeamBufferVoor":null,"TeamBufferNa":null} | {"wedstrijdCode":null,"wedstrijd":"","teamNaam":"","leeftijdsCategorie":null,"competitiesoort":null,"duurMinuten":0,"veldafmeting":0,"huidigeVeld":null,"huidigeTijd":null,"heeftVeld":false,"heeftTijd":false,"optimaalVeldNummer":null,"optimaalVeldNaam":null,"optimaalVeld":null,"optimaalTijd":null,"status":"ongewijzigd","nietInplanbaaarReden":null,"voorkeurTijd":null,"voorkeurAfwijkingMinuten":null,"voorkeurBron":null,"voorkeurStatus":"geen-voorkeur","voorkeurVeldNummer":null,"voorkeurVeldToegepast":null,"teamBufferVoor":null,"teamBufferNa":null}
AutoPlanResponse | Datum:String,TotaalWedstrijden:Int32,ZonderVeld:Int32,ZonderTijd:Int32,TeWijzigen:Int32,NietInplanbaar:Int32,GeschatteEindTijd:String?,Wedstrijden:List<AutoPlanWedstrijdItem>,HuidigeHtml:String,OptimaleHtml:String |  | {"Datum":"","TotaalWedstrijden":0,"ZonderVeld":0,"ZonderTijd":0,"TeWijzigen":0,"NietInplanbaar":0,"GeschatteEindTijd":null,"Wedstrijden":[],"HuidigeHtml":"","OptimaleHtml":""} | {"datum":"","totaalWedstrijden":0,"zonderVeld":0,"zonderTijd":0,"teWijzigen":0,"nietInplanbaar":0,"geschatteEindTijd":null,"wedstrijden":[],"huidigeHtml":"","optimaleHtml":""}
AutoPlanToepassenRequest | Datum:String,BufferMinuten:Nullable<Int32>? |  | {"Datum":"","BufferMinuten":null} | {"datum":"","bufferMinuten":null}
AutoPlanToepassenResponse | Bijgewerkt:Int32,Mislukt:Int32,Fouten:List<String> |  | {"Bijgewerkt":0,"Mislukt":0,"Fouten":[]} | {"bijgewerkt":0,"mislukt":0,"fouten":[]}
""";
}
