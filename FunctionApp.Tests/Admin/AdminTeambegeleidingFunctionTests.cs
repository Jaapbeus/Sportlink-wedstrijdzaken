using FluentAssertions;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

/// <summary>
/// Tests voor AdminTeambegeleidingFunction.ParseCsv — regressietest voor #761:
/// exacte duplicaat-rijen in de bron-CSV mogen niet dubbel geïmporteerd worden.
/// </summary>
public class AdminTeambegeleidingFunctionTests
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

        var result = AdminTeambegeleidingFunction.ParseCsv(csv);

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

        var result = AdminTeambegeleidingFunction.ParseCsv(csv);

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

        var result = AdminTeambegeleidingFunction.ParseCsv(csv);

        result.IsValid.Should().BeTrue();
        result.Rows.Should().HaveCount(2);
        result.Waarschuwingen.Should().NotContain(w => w.Contains("duplicaat"));
    }

    /// <summary>
    /// Regressietests voor #1131 (bevinding 6 uit #1107): een mislukte import mocht de vorige
    /// geldige batch niet wissen. De kolomgrenzen-validatie moet vóór elke destructieve
    /// databasebewerking draaien — deze tests dekken de pure validatiestap
    /// (<see cref="AdminTeambegeleidingFunction.ValideerKolomLengtes"/>) die dat afdwingt, los
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
            var parseResult = AdminTeambegeleidingFunction.ParseCsv(csv);
            parseResult.IsValid.Should().BeTrue();

            var fouten = AdminTeambegeleidingFunction.ValideerKolomLengtes(parseResult.Rows);

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
            var parseResult = AdminTeambegeleidingFunction.ParseCsv(csv);
            parseResult.IsValid.Should().BeTrue();

            var fouten = AdminTeambegeleidingFunction.ValideerKolomLengtes(parseResult.Rows);

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
            var parseResult = AdminTeambegeleidingFunction.ParseCsv(csv);
            parseResult.IsValid.Should().BeTrue();

            var fouten = AdminTeambegeleidingFunction.ValideerKolomLengtes(parseResult.Rows);

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
            var parseResult = AdminTeambegeleidingFunction.ParseCsv(csv);
            parseResult.IsValid.Should().BeTrue();

            var fouten = AdminTeambegeleidingFunction.ValideerKolomLengtes(parseResult.Rows);

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
            var parseResult = AdminTeambegeleidingFunction.ParseCsv(csv);
            parseResult.IsValid.Should().BeTrue();

            var fouten = AdminTeambegeleidingFunction.ValideerKolomLengtes(parseResult.Rows);

            fouten.Should().BeEmpty();
        }
    }
}
