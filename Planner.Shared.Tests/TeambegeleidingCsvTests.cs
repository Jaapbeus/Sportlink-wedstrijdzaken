using AwesomeAssertions;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// Tests voor TeambegeleidingCsv.Parse — regressietest voor #761:
/// exacte duplicaat-rijen in de bron-CSV mogen niet dubbel geïmporteerd worden.
/// </summary>
public class TeambegeleidingCsvTests
{
    private const string Header = "Team;Rol in team;Voornaam;Familienaam;E-mailadres";

    [Fact]
    public void ParseCsv_ExacteDuplicaatRij_WordtEenmaalGeteld()
    {
        var csv = $"""
            {Header}
            JO13-1;Technische staf;Jan;de Vries;trainer@voorbeeld.nl
            JO13-1;Technische staf;Jan;de Vries;trainer@voorbeeld.nl
            """;

        var result = TeambegeleidingCsv.Parse(csv);

        result.IsValid.Should().BeTrue();
        result.Rows.Should().HaveCount(1);
        result.Waarschuwingen.Should().Contain(w => w.Contains("1 exacte duplicaat-rij"));
    }

    [Fact]
    public void ParseCsv_ZelfdePersoonMetTweeRollen_BlijftTweeRijen()
    {
        var csv = $"""
            {Header}
            JO13-1;Technische staf;Jan;de Vries;trainer@voorbeeld.nl
            JO13-1;Overige staf;Jan;de Vries;trainer@voorbeeld.nl
            """;

        var result = TeambegeleidingCsv.Parse(csv);

        result.IsValid.Should().BeTrue();
        result.Rows.Should().HaveCount(2);
        result.Waarschuwingen.Should().NotContain(w => w.Contains("duplicaat"));
    }

    [Fact]
    public void ParseCsv_GeenDuplicaten_GeenWaarschuwing()
    {
        var csv = $"""
            {Header}
            JO13-1;Technische staf;Jan;de Vries;trainer@voorbeeld.nl
            JO13-1;Technische staf;Piet;Jansen;piet@voorbeeld.nl
            """;

        var result = TeambegeleidingCsv.Parse(csv);

        result.IsValid.Should().BeTrue();
        result.Rows.Should().HaveCount(2);
        result.Waarschuwingen.Should().NotContain(w => w.Contains("duplicaat"));
    }

    private const string HeaderMetFunctie = "Team;Teamrol;Functie;Voornaam;Familienaam;E-mailadres";

    [Fact]
    public void ParseCsv_MetFunctieKolom_WordtHerkendEnOvergenomen()
    {
        var csv = $"""
            {HeaderMetFunctie}
            JO13-1;Technische staf;Trainer/coach;Jan;de Vries;trainer@voorbeeld.nl
            """;

        var result = TeambegeleidingCsv.Parse(csv);

        result.IsValid.Should().BeTrue();
        result.Herkend.Should().Contain("Functie");
        result.Rows.Should().ContainSingle().Which.Functie.Should().Be("Trainer/coach");
        result.Waarschuwingen.Should().NotContain(w => w.Contains("Functie"));
    }

    [Fact]
    public void ParseCsv_AliasFunctieInTeam_WordtHerkend()
    {
        var csv = """
            Team;Teamrol;Functie in team;Voornaam;Familienaam;E-mailadres
            JO13-1;Technische staf;Teammanager;Jan;de Vries;trainer@voorbeeld.nl
            """;

        TeambegeleidingCsv.Parse(csv).Rows.Should().ContainSingle().Which.Functie.Should().Be("Teammanager");
    }

    [Fact]
    public void ParseCsv_ZonderFunctieKolom_IsGeldigMetWaarschuwingEnFunctieNull()
    {
        var csv = $"""
            {Header}
            JO13-1;Technische staf;Jan;de Vries;trainer@voorbeeld.nl
            """;

        var result = TeambegeleidingCsv.Parse(csv);

        result.IsValid.Should().BeTrue("Functie is optioneel, exports van vóór #1360 hebben de kolom niet");
        result.Rows.Should().ContainSingle().Which.Functie.Should().BeNull();
        result.Waarschuwingen.Should().Contain(w => w.Contains("'Functie' niet gevonden"));
    }

    [Fact]
    public void ParseCsv_LegeFunctieCel_WordtNull()
    {
        var csv = $"""
            {HeaderMetFunctie}
            JO13-1;Technische staf;;Jan;de Vries;trainer@voorbeeld.nl
            """;

        TeambegeleidingCsv.Parse(csv).Rows.Should().ContainSingle().Which.Functie.Should().BeNull();
    }

    [Fact]
    public void ParseCsv_ZelfdeRijMetVerschillendeFunctie_BlijftTweeRijen()
    {
        var csv = $"""
            {HeaderMetFunctie}
            JO13-1;Technische staf;Trainer/coach;Jan;de Vries;trainer@voorbeeld.nl
            JO13-1;Technische staf;Assistent-trainer/coach;Jan;de Vries;trainer@voorbeeld.nl
            """;

        var result = TeambegeleidingCsv.Parse(csv);

        result.Rows.Should().HaveCount(2, "Functie zit in de dedup-sleutel, anders verdwijnt de tweede functie stil");
        result.Rows.Select(r => r.Functie).Should().BeEquivalentTo(["Trainer/coach", "Assistent-trainer/coach"]);
        result.Waarschuwingen.Should().NotContain(w => w.Contains("duplicaat"));
    }

    [Fact]
    public void ParseCsv_ZelfdeRijMetZelfdeFunctie_WordtGededupliceerd()
    {
        var csv = $"""
            {HeaderMetFunctie}
            JO13-1;Technische staf;Trainer/coach;Jan;de Vries;trainer@voorbeeld.nl
            JO13-1;Technische staf;trainer/COACH;Jan;de Vries;trainer@voorbeeld.nl
            """;

        TeambegeleidingCsv.Parse(csv).Rows.Should().ContainSingle();
    }

    [Fact]
    public void ValideerKolomLengtes_Functie151Tekens_WordtGeweigerd_150Geaccepteerd()
    {
        string Csv(int lengte) => $"""
            {HeaderMetFunctie}
            JO13-1;Technische staf;{new string('F', lengte)};Jan;de Vries;trainer@voorbeeld.nl
            """;

        TeambegeleidingCsv.ValideerKolomLengtes(TeambegeleidingCsv.Parse(Csv(150)).Rows).Should().BeEmpty();
        TeambegeleidingCsv.ValideerKolomLengtes(TeambegeleidingCsv.Parse(Csv(151)).Rows)
            .Should().ContainSingle(f => f.Contains("Functie") && f.Contains("151") && f.Contains("Rij 2"));
    }

    /// <summary>
    /// Regressietests voor #1131 (bevinding 6 uit #1107): een mislukte import mocht de vorige
    /// geldige batch niet wissen. De kolomgrenzen-validatie moet vóór elke destructieve
    /// databasebewerking draaien — deze tests dekken de pure validatiestap
    /// (<see cref="TeambegeleidingCsv.ValideerKolomLengtes"/>) die dat afdwingt, los
    /// van de database-transactie zelf (die vereist een SQL Server-fixture, zie klasse-doc
    /// van de importer-integratietests op de Postgres-tier).
    /// </summary>
    public class ValideerKolomLengtesTests
    {
        private const string Header = "Team;Rol in team;Voornaam;Familienaam;E-mailadres";

        [Fact]
        public void Team_101Tekens_WordtGeweigerd()
        {
            var team = new string('A', 101);
            var csv = $"""
                {Header}
                {team};Technische staf;Jan;de Vries;trainer@voorbeeld.nl
                """;
            var parseResult = TeambegeleidingCsv.Parse(csv);
            parseResult.IsValid.Should().BeTrue();

            var fouten = TeambegeleidingCsv.ValideerKolomLengtes(parseResult.Rows);

            fouten.Should().ContainSingle(f => f.Contains("Team") && f.Contains("101"));
        }

        [Fact]
        public void Team_PreciesMaximaleLengte_WordtGeaccepteerd()
        {
            var team = new string('A', 100);
            var csv = $"""
                {Header}
                {team};Technische staf;Jan;de Vries;trainer@voorbeeld.nl
                """;
            var parseResult = TeambegeleidingCsv.Parse(csv);
            parseResult.IsValid.Should().BeTrue();

            var fouten = TeambegeleidingCsv.ValideerKolomLengtes(parseResult.Rows);

            fouten.Should().BeEmpty();
        }

        [Fact]
        public void Emailadres_TeLang_WordtGeweigerdMetKolomEnRijInMelding()
        {
            var email = new string('x', 190) + "@voorbeeld.nl"; // > 200 tekens
            var csv = $"""
                {Header}
                JO13-1;Technische staf;Jan;de Vries;{email}
                """;
            var parseResult = TeambegeleidingCsv.Parse(csv);
            parseResult.IsValid.Should().BeTrue();

            var fouten = TeambegeleidingCsv.ValideerKolomLengtes(parseResult.Rows);

            fouten.Should().ContainSingle();
            fouten[0].Should().Contain("Rij 2");
            fouten[0].Should().Contain("Emailadres");
        }

        [Fact]
        public void MeerdereRijenTeLang_LevertVoorElkeRijEenAparteFoutOp()
        {
            var teLangTeam = new string('A', 150);
            var csv = $"""
                {Header}
                JO13-1;Technische staf;Jan;de Vries;trainer@voorbeeld.nl
                {teLangTeam};Technische staf;Piet;Jansen;piet@voorbeeld.nl
                """;
            var parseResult = TeambegeleidingCsv.Parse(csv);
            parseResult.IsValid.Should().BeTrue();

            var fouten = TeambegeleidingCsv.ValideerKolomLengtes(parseResult.Rows);

            fouten.Should().ContainSingle();
            fouten[0].Should().Contain("Rij 3", "de tweede datarij is rij 3 (rij 1 = header)");
        }

        [Fact]
        public void AlleVeldenBinnenGrens_GeenFouten()
        {
            var csv = $"""
                {Header}
                JO13-1;Technische staf;Jan;de Vries;trainer@voorbeeld.nl
                """;
            var parseResult = TeambegeleidingCsv.Parse(csv);
            parseResult.IsValid.Should().BeTrue();

            var fouten = TeambegeleidingCsv.ValideerKolomLengtes(parseResult.Rows);

            fouten.Should().BeEmpty();
        }

        // #1461: rijnummers moeten naar de regel in het ORIGINELE bestand wijzen, ook na lege regels
        // en deduplicatie; ParseEnValideer is de ingang voor beide tiers.
        [Fact]
        public void ParseEnValideer_RijnummerIsOrigineleRegel_NaLegeRegelsEnDuplicaten()
        {
            var csv = Header + "\n"
                + "JO13-1;Technische staf;Jan;de Vries;trainer@voorbeeld.nl\n"
                + "\n"
                + "JO13-1;Technische staf;Jan;de Vries;trainer@voorbeeld.nl\n"
                + $"{new string('A', 101)};Technische staf;Piet;Jansen;piet@voorbeeld.nl\n";
            var r = TeambegeleidingCsv.ParseEnValideer(csv);

            r.IsValid.Should().BeTrue();
            r.Lengtefouten.Should().ContainSingle().Which.Should().Contain("Rij 5");
        }

        [Fact]
        public void DuplicaatWaarschuwing_NoemtTelefoonnummer()
        {
            var csv = "Team;Rol in team;Voornaam;Familienaam;E-mailadres\n"
                + "JO13-1;Staf;Jan;de Vries;a@voorbeeld.nl\n"
                + "JO13-1;Staf;Jan;de Vries;a@voorbeeld.nl\n";
            TeambegeleidingCsv.Parse(csv).Waarschuwingen.Should().Contain(w => w.Contains("telefoonnummer"));
        }
    }
}
