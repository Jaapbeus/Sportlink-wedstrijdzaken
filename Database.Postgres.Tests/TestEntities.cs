using Database.Postgres;

namespace Database.Postgres.Tests;

/// <summary>Gedeelde testfixtures — vier scenario's per #818's testplan: single-/multi-kolom
/// business key, met/zonder ClubCode.</summary>
internal static class TestEntities
{
    /// <summary>Single-kolom key, geen ClubCode.</summary>
    public static EntityDefinition SingleKeyNoClub => EntityDefinition.Create(
        entityName: "matches",
        columns:
        [
            new ColumnDefinition("matchcode", ProviderAgnosticType.VarChar, IsNullable: false, Length: 50),
            new ColumnDefinition("datum", ProviderAgnosticType.Timestamp, IsNullable: true),
        ],
        businessKey: ["matchcode"],
        hasClubCode: false);

    /// <summary>Single-kolom key, met ClubCode.</summary>
    public static EntityDefinition SingleKeyWithClub => EntityDefinition.Create(
        entityName: "matches",
        columns:
        [
            new ColumnDefinition("matchcode", ProviderAgnosticType.VarChar, IsNullable: false, Length: 50),
            new ColumnDefinition("datum", ProviderAgnosticType.Timestamp, IsNullable: true),
            new ColumnDefinition("clubcode", ProviderAgnosticType.VarChar, IsNullable: false, Length: 20),
        ],
        businessKey: ["matchcode"],
        hasClubCode: true);

    /// <summary>Multi-kolom key (NULL-bare kolommen, zoals teams.poulecode in productie), geen ClubCode.</summary>
    public static EntityDefinition MultiKeyNoClub => EntityDefinition.Create(
        entityName: "teams",
        columns:
        [
            new ColumnDefinition("teamcode", ProviderAgnosticType.VarChar, IsNullable: true, Length: 50),
            new ColumnDefinition("lokaleteamcode", ProviderAgnosticType.VarChar, IsNullable: true, Length: 50),
            new ColumnDefinition("poulecode", ProviderAgnosticType.VarChar, IsNullable: true, Length: 50),
            new ColumnDefinition("teamnaam", ProviderAgnosticType.Text, IsNullable: true),
        ],
        businessKey: ["teamcode", "lokaleteamcode", "poulecode"],
        hasClubCode: false);

    /// <summary>Multi-kolom key, met ClubCode.</summary>
    public static EntityDefinition MultiKeyWithClub => EntityDefinition.Create(
        entityName: "teams",
        columns:
        [
            new ColumnDefinition("teamcode", ProviderAgnosticType.VarChar, IsNullable: true, Length: 50),
            new ColumnDefinition("lokaleteamcode", ProviderAgnosticType.VarChar, IsNullable: true, Length: 50),
            new ColumnDefinition("poulecode", ProviderAgnosticType.VarChar, IsNullable: true, Length: 50),
            new ColumnDefinition("teamnaam", ProviderAgnosticType.Text, IsNullable: true),
            new ColumnDefinition("clubcode", ProviderAgnosticType.VarChar, IsNullable: false, Length: 20),
        ],
        businessKey: ["teamcode", "lokaleteamcode", "poulecode"],
        hasClubCode: true);
}
