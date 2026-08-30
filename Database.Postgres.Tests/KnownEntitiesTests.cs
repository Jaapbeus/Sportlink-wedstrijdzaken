using Database.Postgres;
using FluentAssertions;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Bewijst dat de generator werkt voor alle drie de daadwerkelijk bestaande entiteiten
/// (#818-acceptatiecriterium), niet alleen voor synthetische testfixtures.
/// </summary>
public class KnownEntitiesTests
{
    [Theory]
    [MemberData(nameof(AllKnownEntities))]
    public void GenerateStgTable_VoorElkeBekendeEntiteit_GenereertZonderException(EntityDefinition entity)
    {
        var act = () => PostgresSchemaGenerator.GenerateStgTable(entity);

        act.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(AllKnownEntities))]
    public void GenerateHisTable_VoorElkeBekendeEntiteit_GenereertZonderExceptionEnBevatBkKolom(EntityDefinition entity)
    {
        var sql = PostgresSchemaGenerator.GenerateHisTable(entity);

        sql.Should().Contain($"bk_{entity.EntityName}");
        sql.Should().Contain("CREATE UNIQUE INDEX");
    }

    [Theory]
    [MemberData(nameof(AllKnownEntities))]
    public void GenerateUpsertFromStgToHis_VoorElkeBekendeEntiteit_GenereertZonderException(EntityDefinition entity)
    {
        var act = () => PostgresUpsertGenerator.GenerateUpsertFromStgToHis(entity);

        act.Should().NotThrow();
    }

    [Fact]
    public void MatchDetails_BusinessKeyIsIntegerKolom_WerktOokAlsHetGeenStringIs()
    {
        // WedstrijdCode is INT, niet VARCHAR — de synthetische bk_-kolom cast expliciet naar
        // ::text, dus dit mag geen probleem zijn. Zie ook de empirische integratietest.
        var sql = PostgresSchemaGenerator.GenerateHisTable(KnownEntities.MatchDetails);

        sql.Should().Contain("COALESCE(\"WedstrijdCode\"::text, '')");
    }

    public static TheoryData<EntityDefinition> AllKnownEntities() =>
        new(KnownEntities.All);
}
