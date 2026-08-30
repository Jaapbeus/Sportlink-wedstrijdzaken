using Database.Postgres;
using FluentAssertions;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// #818's testplan eist minimaal vier scenario's: single-/multi-kolom business key, met/zonder
/// ClubCode-kolom. Elke test hieronder dekt er één (of test alle vier tegelijk waar dat zinvol is).
/// </summary>
public class PostgresSchemaGeneratorTests
{
    [Fact]
    public void GenerateStgTable_BevatGeenAuditKolommenEnGeenUniqueConstraint()
    {
        var sql = PostgresSchemaGenerator.GenerateStgTable(TestEntities.SingleKeyNoClub);

        sql.Should().Contain("DROP TABLE IF EXISTS stg.\"matches\";");
        sql.Should().Contain("CREATE TABLE stg.\"matches\" (");
        sql.Should().Contain("\"matchcode\" VARCHAR(50) NOT NULL");
        sql.Should().Contain("\"datum\" TIMESTAMP");
        sql.Should().NotContain("mta_inserted");
        sql.Should().NotContain("bk_matches");
        sql.Should().NotContain("UNIQUE");
    }

    [Fact]
    public void GenerateHisTable_SingleKeyNoClub_BevatIdAuditEnUniekeBkIndex_GeenClubCodeIndex()
    {
        var sql = PostgresSchemaGenerator.GenerateHisTable(TestEntities.SingleKeyNoClub);

        sql.Should().Contain("CREATE TABLE IF NOT EXISTS his.\"matches\" (");
        sql.Should().NotContain("IDENTITY");
        sql.Should().NotContain("PRIMARY KEY");
        sql.Should().Contain("\"matchcode\" VARCHAR(50) NOT NULL,");
        sql.Should().Contain("\"mta_inserted\" TIMESTAMP NOT NULL,");
        sql.Should().Contain("\"mta_modified\" TIMESTAMP NOT NULL,");
        sql.Should().Contain("\"mta_deleted\" TIMESTAMP NULL,");
        sql.Should().Contain("\"bk_matches\" TEXT GENERATED ALWAYS AS (COALESCE(\"matchcode\"::text, '')) STORED");
        sql.Should().Contain("CREATE UNIQUE INDEX IF NOT EXISTS \"UQ_matches_bk\" ON his.\"matches\" (\"bk_matches\");");
        sql.Should().NotContain("ClubCode");
    }

    [Fact]
    public void GenerateHisTable_SingleKeyWithClub_BevatClubCodeIndex()
    {
        var sql = PostgresSchemaGenerator.GenerateHisTable(TestEntities.SingleKeyWithClub);

        sql.Should().Contain("\"ClubCode\" VARCHAR(20) NOT NULL,");
        sql.Should().Contain("CREATE INDEX IF NOT EXISTS \"IX_matches_ClubCode\" ON his.\"matches\" (\"ClubCode\");");
    }

    [Fact]
    public void GenerateHisTable_MultiKeyNoClub_CombineertAlleSleutelKolommenMetScheidingsteken()
    {
        var sql = PostgresSchemaGenerator.GenerateHisTable(TestEntities.MultiKeyNoClub);

        sql.Should().Contain(
            "\"bk_teams\" TEXT GENERATED ALWAYS AS (" +
            "COALESCE(\"teamcode\"::text, '') || '' || " +
            "COALESCE(\"lokaleteamcode\"::text, '') || '' || " +
            "COALESCE(\"poulecode\"::text, '')) STORED");
        sql.Should().NotContain("ClubCode");
    }

    [Fact]
    public void GenerateHisTable_MultiKeyWithClub_BevatZowelSyntheticKeyAlsClubCodeIndex()
    {
        var sql = PostgresSchemaGenerator.GenerateHisTable(TestEntities.MultiKeyWithClub);

        sql.Should().Contain("\"bk_teams\" TEXT GENERATED ALWAYS AS (");
        sql.Should().Contain("CREATE INDEX IF NOT EXISTS \"IX_teams_ClubCode\" ON his.\"teams\" (\"ClubCode\");");
    }

    [Fact]
    public void GenerateHisTable_NullableBusinessKeyKolommen_WordenNietNotNullMaarWelInSyntheticKeyGecoalesced()
    {
        // Kern van het #818-addendum: de business-key-kolommen zelf blijven nullable (matcht de
        // productie-data, bijv. teams.poulecode), maar de synthetische bk_-kolom is dat nooit —
        // dat is precies wat GenerateHisTable_MultiKeyNoClub hierboven al aantoont via COALESCE.
        var sql = PostgresSchemaGenerator.GenerateHisTable(TestEntities.MultiKeyNoClub);

        sql.Should().Contain("\"teamcode\" VARCHAR(50),");
        sql.Should().NotContain("\"teamcode\" VARCHAR(50) NOT NULL");
    }
}
