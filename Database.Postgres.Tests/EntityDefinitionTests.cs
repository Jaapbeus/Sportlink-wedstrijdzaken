using Database.Postgres;
using FluentAssertions;
using Xunit;

namespace Database.Postgres.Tests;

public class EntityDefinitionTests
{
    [Fact]
    public void Create_BusinessKeyVerwijstNaarOnbekendeKolom_GooitArgumentException()
    {
        var act = () => EntityDefinition.Create(
            "teams",
            [new ColumnDefinition("teamnaam", ProviderAgnosticType.Text)],
            businessKey: ["teamcode"],
            hasClubCode: false);

        act.Should().Throw<ArgumentException>().WithMessage("*teamcode*");
    }

    [Fact]
    public void Create_HasClubCodeZonderClubCodeKolom_GooitArgumentException()
    {
        var act = () => EntityDefinition.Create(
            "teams",
            [new ColumnDefinition("teamnaam", ProviderAgnosticType.Text)],
            businessKey: ["teamnaam"],
            hasClubCode: true);

        act.Should().Throw<ArgumentException>().WithMessage("*ClubCode*");
    }

    [Fact]
    public void Create_GeenBusinessKey_GooitArgumentException()
    {
        var act = () => EntityDefinition.Create(
            "teams",
            [new ColumnDefinition("teamnaam", ProviderAgnosticType.Text)],
            businessKey: [],
            hasClubCode: false);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_GeldigeDefinitie_Slaagt()
    {
        var entity = TestEntities.MultiKeyWithClub;

        entity.EntityName.Should().Be("teams");
        entity.BusinessKey.Should().Equal("teamcode", "lokaleteamcode", "poulecode");
        entity.HasClubCode.Should().BeTrue();
    }
}
