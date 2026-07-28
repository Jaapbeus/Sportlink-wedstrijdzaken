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
}
