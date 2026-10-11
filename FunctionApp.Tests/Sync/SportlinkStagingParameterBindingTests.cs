using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using SportlinkFunction;
using System.Globalization;
using Xunit;

namespace FunctionApp.Tests.Sync;

/// <summary>
/// Karakterisatietest (#1624) voor SportlinkStagingRepository.BindMatchDetailsParameters:
/// legt per parameter naam, DBNull-status en waarde vast, zonder database. De golden-strings zijn
/// vastgezet op de ongewijzigde implementatie; een refactor mag ze niet veranderen.
/// </summary>
public class SportlinkStagingParameterBindingTests
{
    private static string Beschrijf(SqlCommand cmd) =>
        string.Join("\n", cmd.Parameters.Cast<SqlParameter>()
            .OrderBy(p => p.ParameterName, StringComparer.Ordinal)
            .Select(p => p.Value is DBNull
                ? $"{p.ParameterName}|DBNull"
                : $"{p.ParameterName}|{p.Value!.GetType().Name}|{Tekst(p.Value)}"));

    private static string Tekst(object waarde) => waarde switch
    {
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => waarde.ToString() ?? string.Empty,
    };

    private static MatchDetails VolGevuld() => new()
    {
        Wedstrijdinformatie = new Wedstrijdinformatie
        {
            Wedstrijdnummer = 1,
            Wedstijdnummerintern = 2,
            Veldnaam = "Veld 1",
            Veldlocatie = "Hoofdveld",
            Vertrektijd = "08:30",
            Rijder = "Jan de Vries",
            Thuisscore = "3",
            ThuisscoreRegulier = "2",
            ThuisscoreNv = "1",
            ThuisscoreS = "0",
            Uitscore = "4",
            UitscoreRegulier = "3",
            UitscoreNv = "1",
            UitscoreS = "0",
            Klasse = "Klasse A",
            Wedstrijdtype = "Competitie",
            Competitietype = "Veld",
            Categorie = "Senioren",
            Wedstrijddatetime = new DateTime(2026, 3, 14, 10, 0, 0, DateTimeKind.Utc),
            Wedstrijddatum = new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc),
            Aanvangstijd = "10:00",
            Duur = 90,
            Speltype = "11 tegen 11",
            Aanduiding = "A1",
            Poulecode = "42",
            Poule = "Poule 1",
            Thuisteamid = 11,
            Thuisteam = "ALLSTARS 1",
            Uitteamid = 12,
            Uitteam = "Tegenstander 1",
            Opmerkingen = "Geen",
        },
        Officials = new Officials
        {
            Verenigingsscheidsrechtercode = "VS1",
            Verenigingsscheidsrechter = "Jan de Vries",
            Overigeofficialcode = "OO1",
            Overigeofficial = "Jan de Vries",
        },
        Matchofficials = new Matchofficials { Scheidsrechters = "Jan de Vries" },
        Kleedkamers = new Kleedkamers { Thuis = "K1", Uit = "K2", Official = "K3" },
        Accommodatie = new Accommodatie
        {
            Naam = "Sportpark", Straat = "Sportlaan 1", Plaats = "Teststad",
            Telefoon = "0000000000", Routeplanner = "route",
        },
        Thuisteam = Team<Thuisteam>("Thuis"),
        Uitteam = Team<Uitteam>("Uit"),
    };

    private static T Team<T>(string prefix) where T : new()
    {
        var t = new T();
        var type = typeof(T);
        type.GetProperty("Naam")!.SetValue(t, prefix + "naam");
        type.GetProperty("Code")!.SetValue(t, prefix + "code");
        type.GetProperty("Website")!.SetValue(t, "https://allstars-fc.test/" + prefix);
        type.GetProperty("Shirtkleur")!.SetValue(t, prefix + "kleur");
        type.GetProperty("Straat")!.SetValue(t, prefix + "straat");
        type.GetProperty("Postcodeplaats")!.SetValue(t, prefix + "plaats");
        type.GetProperty("Telefoon")!.SetValue(t, "0000000000");
        type.GetProperty("Email")!.SetValue(t, "trainer@voorbeeld.nl");
        return t;
    }

    private static MatchDetails AllesNull()
    {
        var m = new MatchDetails();
        foreach (var obj in new object[] { m.Wedstrijdinformatie, m.Officials, m.Matchofficials, m.Kleedkamers, m.Accommodatie, m.Thuisteam, m.Uitteam })
            foreach (var p in obj.GetType().GetProperties().Where(p => p.PropertyType == typeof(string)))
                p.SetValue(obj, null);
        return m;
    }

    [Fact]
    public void VolGevuldObject_BindtAlleParametersMetWaarde()
    {
        using var cmd = new SqlCommand();
        SportlinkStagingRepository.BindMatchDetailsParameters(cmd, VolGevuld(), "ALLSTARS");
        Beschrijf(cmd).Should().Be(GoldenVolGevuld);
    }

    [Fact]
    public void ObjectMetOveralNull_BindtDBNullOfParseFallback()
    {
        using var cmd = new SqlCommand();
        SportlinkStagingRepository.BindMatchDetailsParameters(cmd, AllesNull(), "ALLSTARS");
        Beschrijf(cmd).Should().Be(GoldenAllesNull);
    }

    private const string GoldenVolGevuld = """
    @Aanduiding|String|A1
    @Aanvangstijd|TimeSpan|10:00:00
    @AccommodatieNaam|String|Sportpark
    @AccommodatiePlaats|String|Teststad
    @AccommodatieRouteplanner|String|route
    @AccommodatieStraat|String|Sportlaan 1
    @AccommodatieTelefoon|String|0000000000
    @Categorie|String|Senioren
    @ClubCode|String|ALLSTARS
    @CompetitieType|String|Veld
    @Duration|Int32|90
    @InternCode|Int32|2
    @Klasse|String|Klasse A
    @KleedkamerOfficial|String|K3
    @KleedkamerThuis|String|K1
    @KleedkamerUit|String|K2
    @MatchDate|DateTime|2026-03-14T00:00:00.0000000Z
    @MatchDateTime|DateTime|2026-03-14T10:00:00.0000000Z
    @Opmerkingen|String|Geen
    @OverigeOfficial|String|Jan de Vries
    @OverigeOfficialCode|String|OO1
    @Poule|String|Poule 1
    @PouleCode|Int32|42
    @Rijder|String|Jan de Vries
    @Scheidsrechters|String|Jan de Vries
    @SpelType|String|11 tegen 11
    @ThuisScore|String|3
    @ThuisScoreNV|String|1
    @ThuisScoreRegulier|String|2
    @ThuisScoreS|String|0
    @ThuisTeam|String|ALLSTARS 1
    @ThuisTeamCode|String|Thuiscode
    @ThuisTeamEmail|String|trainer@voorbeeld.nl
    @ThuisTeamID|Int32|11
    @ThuisTeamNaam|String|Thuisnaam
    @ThuisTeamPostcodePlaats|String|Thuisplaats
    @ThuisTeamShirtKleur|String|Thuiskleur
    @ThuisTeamStraat|String|Thuisstraat
    @ThuisTeamTelefoon|String|0000000000
    @ThuisTeamWebsite|String|https://allstars-fc.test/Thuis
    @UitScore|String|4
    @UitScoreNV|String|1
    @UitScoreRegulier|String|3
    @UitScoreS|String|0
    @UitTeam|String|Tegenstander 1
    @UitTeamCode|String|Uitcode
    @UitTeamEmail|String|trainer@voorbeeld.nl
    @UitTeamID|Int32|12
    @UitTeamNaam|String|Uitnaam
    @UitTeamPostcodePlaats|String|Uitplaats
    @UitTeamShirtKleur|String|Uitkleur
    @UitTeamStraat|String|Uitstraat
    @UitTeamTelefoon|String|0000000000
    @UitTeamWebsite|String|https://allstars-fc.test/Uit
    @VeldLocatie|String|Hoofdveld
    @VeldNaam|String|Veld 1
    @VerenigingScheidsrechter|String|Jan de Vries
    @VerenigingScheidsrechterCode|String|VS1
    @VertrekTijd|String|08:30
    @WedstrijdCode|Int32|1
    @WedstrijdType|String|Competitie
    """;
    private const string GoldenAllesNull = """
    @Aanduiding|DBNull
    @Aanvangstijd|DBNull
    @AccommodatieNaam|DBNull
    @AccommodatiePlaats|DBNull
    @AccommodatieRouteplanner|DBNull
    @AccommodatieStraat|DBNull
    @AccommodatieTelefoon|DBNull
    @Categorie|DBNull
    @ClubCode|String|ALLSTARS
    @CompetitieType|DBNull
    @Duration|DBNull
    @InternCode|Int32|0
    @Klasse|DBNull
    @KleedkamerOfficial|DBNull
    @KleedkamerThuis|DBNull
    @KleedkamerUit|DBNull
    @MatchDate|DBNull
    @MatchDateTime|DBNull
    @Opmerkingen|DBNull
    @OverigeOfficial|DBNull
    @OverigeOfficialCode|DBNull
    @Poule|DBNull
    @PouleCode|DBNull
    @Rijder|DBNull
    @Scheidsrechters|DBNull
    @SpelType|DBNull
    @ThuisScore|DBNull
    @ThuisScoreNV|DBNull
    @ThuisScoreRegulier|DBNull
    @ThuisScoreS|DBNull
    @ThuisTeam|DBNull
    @ThuisTeamCode|DBNull
    @ThuisTeamEmail|DBNull
    @ThuisTeamID|Int32|0
    @ThuisTeamNaam|DBNull
    @ThuisTeamPostcodePlaats|DBNull
    @ThuisTeamShirtKleur|DBNull
    @ThuisTeamStraat|DBNull
    @ThuisTeamTelefoon|DBNull
    @ThuisTeamWebsite|DBNull
    @UitScore|DBNull
    @UitScoreNV|DBNull
    @UitScoreRegulier|DBNull
    @UitScoreS|DBNull
    @UitTeam|DBNull
    @UitTeamCode|DBNull
    @UitTeamEmail|DBNull
    @UitTeamID|Int32|0
    @UitTeamNaam|DBNull
    @UitTeamPostcodePlaats|DBNull
    @UitTeamShirtKleur|DBNull
    @UitTeamStraat|DBNull
    @UitTeamTelefoon|DBNull
    @UitTeamWebsite|DBNull
    @VeldLocatie|DBNull
    @VeldNaam|DBNull
    @VerenigingScheidsrechter|DBNull
    @VerenigingScheidsrechterCode|DBNull
    @VertrekTijd|DBNull
    @WedstrijdCode|Int32|0
    @WedstrijdType|DBNull
    """;
}
