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
        // wedstrijdcode is INT, niet VARCHAR — de synthetische bk_-kolom cast expliciet naar
        // ::text, dus dit mag geen probleem zijn. Zie ook de empirische integratietest.
        var sql = PostgresSchemaGenerator.GenerateHisTable(KnownEntities.MatchDetails);

        sql.Should().Contain("COALESCE(\"wedstrijdcode\"::text, '')");
    }

    /// <summary>
    /// #855: bewaakt de lowercase-snake_case-conventie (docs/ARCHITECTUUR-DATABASE-TIERS.md §3)
    /// tegen elke kolomnaam die <see cref="KnownEntities"/> daadwerkelijk oplevert — niet alleen
    /// tegen de synthetische <see cref="TestEntities"/>-fixtures. Bewust beperkt tot kolomnamen
    /// (het "informatie_schema.columns"-equivalent van de acceptatiecriteria), niet tot de door
    /// <see cref="PostgresSchemaGenerator"/> zelf toegevoegde <c>UQ_</c>/<c>IX_</c>-indexnamen — die
    /// dragen bewust de bestaande, SQL-Server-gespiegelde prefix-conventie (zie het issue zelf,
    /// dat "UQ_teams_bk" ongewijzigd als voorbeeld citeert) en worden nergens elders via een
    /// handgeschreven, ongequote query aangesproken — anders dan kolomnamen. Een eerdere versie van
    /// <c>KnownEntities</c> week af voor <c>ClubCode</c> en de volledige <c>matchdetails</c>-
    /// entiteit; <see cref="EntityDefinition.Create"/> voorkomt dat nu al bij constructie, maar deze
    /// test bewaakt tegen regressie op de daadwerkelijk gegenereerde DDL, niet alleen de C#-invoer.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllKnownEntities))]
    public void GenerateHisTable_VoorElkeBekendeEntiteit_GeenEnkeleKolomnaamWijktAfVanLowercase(
        EntityDefinition entity)
    {
        var sql = PostgresSchemaGenerator.GenerateHisTable(entity);
        var kolomIdentifiers = System.Text.RegularExpressions.Regex.Matches(sql,
                "\"([^\"]+)\"\\s+(?:VARCHAR|INTEGER|BIGINT|TEXT|TIMESTAMPTZ|DATE|TIME|DECIMAL|BOOLEAN)")
            .Select(m => m.Groups[1].Value)
            .Distinct();

        kolomIdentifiers.Should().NotBeEmpty("de regex moet daadwerkelijk kolomdefinities matchen");
        foreach (var identifier in kolomIdentifiers)
            identifier.Should().Be(identifier.ToLowerInvariant(),
                $"kolomnaam '{identifier}' in de gegenereerde his-DDL voor '{entity.EntityName}' " +
                "moet lowercase zijn (ARCHITECTUUR-DATABASE-TIERS.md §3) — anders landt de afwijkende " +
                "casing letterlijk in de database en breekt elke latere, ongequote verwijzing.");
    }

    public static TheoryData<EntityDefinition> AllKnownEntities() =>
        new(KnownEntities.All);
}
