using Database.Postgres;
using FluentAssertions;
using Xunit;

namespace Database.Postgres.Tests;

public class PostgresUpsertGeneratorTests
{
    [Fact]
    public void GenerateUpsertFromStgToHis_SingleKeyNoClub_GebruiktSyntheticBkAlsConflictTarget()
    {
        var sql = PostgresUpsertGenerator.GenerateUpsertFromStgToHis(TestEntities.SingleKeyNoClub);

        sql.Should().Contain("INSERT INTO his.\"matches\" (\"matchcode\", \"datum\", \"mta_inserted\", \"mta_modified\")");
        sql.Should().Contain("SELECT \"matchcode\", \"datum\", NOW(), NOW()");
        sql.Should().Contain("FROM stg.\"matches\"");
        sql.Should().Contain("ON CONFLICT (\"bk_matches\") DO UPDATE SET");
        sql.Should().Contain("\"matchcode\" = EXCLUDED.\"matchcode\"");
        sql.Should().Contain("\"datum\" = EXCLUDED.\"datum\"");
        sql.Should().Contain("\"mta_modified\" = NOW()");
    }

    [Fact]
    public void GenerateUpsertFromStgToHis_ChangeDetection_DektAlleenDataKolommen_NietAudit()
    {
        var sql = PostgresUpsertGenerator.GenerateUpsertFromStgToHis(TestEntities.SingleKeyNoClub);

        sql.Should().Contain(
            "WHERE his.\"matches\".\"matchcode\" IS DISTINCT FROM EXCLUDED.\"matchcode\" OR " +
            "his.\"matches\".\"datum\" IS DISTINCT FROM EXCLUDED.\"datum\"");
        sql.Should().NotContain("mta_inserted\" IS DISTINCT");
        sql.Should().NotContain("mta_modified\" IS DISTINCT");
        sql.Should().NotContain("mta_deleted\" IS DISTINCT");
    }

    [Fact]
    public void GenerateUpsertFromStgToHis_MetClubCode_NeemtClubCodeMeeAlsGewoneDataKolom()
    {
        var sql = PostgresUpsertGenerator.GenerateUpsertFromStgToHis(TestEntities.SingleKeyWithClub);

        sql.Should().Contain("\"ClubCode\"");
        sql.Should().Contain("\"ClubCode\" = EXCLUDED.\"ClubCode\"");
    }

    [Fact]
    public void GenerateUpsertFromStgToHis_MultiKey_GebruiktNogSteedsÉénSyntheticBkAlsConflictTarget()
    {
        var sql = PostgresUpsertGenerator.GenerateUpsertFromStgToHis(TestEntities.MultiKeyNoClub);

        sql.Should().Contain("ON CONFLICT (\"bk_teams\") DO UPDATE SET");
    }
}
