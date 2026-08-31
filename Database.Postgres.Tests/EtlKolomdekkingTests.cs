using System.Text.RegularExpressions;
using Database.Postgres;
using FluentAssertions;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// #864, deel 3 — de kolomdekking van de zes dynamisch aangemaakte ETL-tabellen
/// (<c>his.teams</c>/<c>matches</c>/<c>matchdetails</c> en hun <c>stg</c>-tegenhangers).
///
/// <para>
/// <b>Waarom dit een test is en geen shell-script, anders dan de andere 19 tabellen.</b>
/// <c>scripts/ci/check-postgres-column-coverage.sh</c> vergelijkt <c>Database/**/Tables/*.sql</c>
/// met <c>Database.Postgres/migrations/*.sql</c>. Deze zes tabellen staan in géén migratie: ze
/// worden op sync-tijd gegenereerd door <see cref="PostgresSchemaGenerator"/> uit
/// <see cref="KnownEntities"/> (#818). Een shell-script zou daarvoor de C#-lijst opnieuw moeten
/// parseren; deze test roept de échte generator aan en leest de kolommen uit de DDL die in
/// productie ook daadwerkelijk wordt uitgevoerd. Dat is een sterker bewijs, niet alleen een
/// makkelijker.
/// </para>
///
/// <para>
/// Richting: uitsluitend "SQL Server heeft een kolom die Postgres mist" — zelfde redenering als
/// beide dekkingsscripts: de Postgres-boom is een vertaling VAN de SQL Server-boom.
/// De omgekeerde richting is bewust geen fout: <c>stg.*</c> krijgt op de Postgres-tier een
/// <c>clubcode</c>-kolom die de SQL Server-tegenhanger niet heeft (daar komt de ClubCode pas bij
/// de merge naar <c>his.*</c>), en dat is een bewust verschil, geen drift.
/// </para>
/// </summary>
public class EtlKolomdekkingTests
{
    /// <summary>
    /// Kolommen die bewust géén 1-op-1-tegenhanger hebben. Sleutel: "his|stg.entiteit.kolom"
    /// (lowercase). Elke rij is een vastgelegde beslissing met een reden — geen omissie.
    /// </summary>
    private static readonly Dictionary<string, string> BewusteAfwijkingen = new()
    {
        ["his.matchdetails.bk_wedstrijdcode"] =
            "De SQL Server-tier is hier zelf inconsistent: his.Teams en his.Matches gebruiken " +
            "bk_<entiteit> (bk_teams, bk_matches), maar his.MatchDetails gebruikt de naam van de " +
            "business-key-KOLOM (bk_WedstrijdCode). PostgresSchemaGenerator.BusinessKeyColumnName " +
            "hanteert consequent bk_<entiteit> voor alle drie, dus daar heet hij bk_matchdetails. " +
            "Niets buiten de SQL Server-boom verwijst naar bk_WedstrijdCode (alleen " +
            "mta.source_target_mapping en Script.PostDeployment1.sql, en de Postgres-tier heeft " +
            "die stuurtabel architecturaal niet — #818). Bewust niet gespiegeld: de inconsistentie " +
            "overnemen zou de Postgres-boom onnodig onregelmatig maken.",
    };

    public static TheoryData<string, string, EntityDefinition> HisTabellen() => new()
    {
        { "his", "Database/his/Tables/Teams.sql", KnownEntities.Teams },
        { "his", "Database/his/Tables/Matches.sql", KnownEntities.Matches },
        { "his", "Database/his/Tables/MatchDetails.sql", KnownEntities.MatchDetails },
    };

    public static TheoryData<string, string, EntityDefinition> StgTabellen() => new()
    {
        { "stg", "Database/stg/Tables/Teams.sql", KnownEntities.Teams },
        { "stg", "Database/stg/Tables/Matches.sql", KnownEntities.Matches },
        { "stg", "Database/stg/Tables/MatchDetails.sql", KnownEntities.MatchDetails },
    };

    [Theory]
    [MemberData(nameof(HisTabellen))]
    public void HisTabel_DektElkeKolomVanDeSqlServerTegenhanger(
        string schema, string sqlServerPad, EntityDefinition entiteit)
        => VergelijkKolommen(schema, sqlServerPad, PostgresSchemaGenerator.GenerateHisTable(entiteit));

    [Theory]
    [MemberData(nameof(StgTabellen))]
    public void StgTabel_DektElkeKolomVanDeSqlServerTegenhanger(
        string schema, string sqlServerPad, EntityDefinition entiteit)
        => VergelijkKolommen(schema, sqlServerPad, PostgresSchemaGenerator.GenerateStgTable(entiteit));

    private static void VergelijkKolommen(string schema, string sqlServerPad, string postgresDdl)
    {
        var sqlServerKolommen = LeesSqlServerKolommen(sqlServerPad);
        var postgresKolommen = LeesPostgresKolommen(postgresDdl);

        // Zonder deze twee ondergrenzen zou een kapotte parser een lege verzameling opleveren en
        // zou de vergelijking hieronder triviaal slagen — de "nul asserties = groen"-val.
        sqlServerKolommen.Should().NotBeEmpty($"{sqlServerPad} moet kolommen bevatten");
        postgresKolommen.Should().NotBeEmpty("de gegenereerde Postgres-DDL moet kolommen bevatten");

        var ontbrekend = sqlServerKolommen
            .Where(k => !postgresKolommen.Contains(k))
            .Where(k => !BewusteAfwijkingen.ContainsKey($"{schema}.{entiteitUit(sqlServerPad)}.{k}"))
            .ToList();

        ontbrekend.Should().BeEmpty(
            $"elke kolom van {sqlServerPad} moet een tegenhanger hebben in de door " +
            $"PostgresSchemaGenerator gegenereerde {schema}-tabel, of als bewuste afwijking in " +
            $"{nameof(BewusteAfwijkingen)} staan (#864). Ontbrekend: {string.Join(", ", ontbrekend)}");
    }

    private static string entiteitUit(string sqlServerPad)
        => Path.GetFileNameWithoutExtension(sqlServerPad).ToLowerInvariant();

    /// <summary>
    /// Kolomnamen uit een SQL Server-tabelbestand: het eerste token op elke regel binnen de
    /// CREATE TABLE-body, met of zonder blokhaken; constraintregels overgeslagen. Zelfde regels
    /// als de parser in check-postgres-column-coverage.sh, hier in C#.
    /// </summary>
    private static HashSet<string> LeesSqlServerKolommen(string relatiefPad)
    {
        var pad = Path.Combine(RepoRoot(), relatiefPad);
        File.Exists(pad).Should().BeTrue($"{relatiefPad} moet bestaan");

        var kolommen = new HashSet<string>(StringComparer.Ordinal);
        var inBody = false;
        foreach (var ruw in File.ReadAllLines(pad))
        {
            var regel = ZonderCommentaar(ruw);
            if (!inBody)
            {
                if (Regex.IsMatch(regel, @"CREATE\s+TABLE", RegexOptions.IgnoreCase)) inBody = true;
                continue;
            }
            if (Regex.IsMatch(regel, @"^\s*\)")) break;
            if (Regex.IsMatch(regel, @"^\s*(CONSTRAINT|PRIMARY|UNIQUE|CHECK|FOREIGN|INDEX)\s", RegexOptions.IgnoreCase))
                continue;

            var m = Regex.Match(regel, @"^\s*\[?([A-Za-z_][A-Za-z0-9_]*)\]?");
            if (m.Success) kolommen.Add(m.Groups[1].Value.ToLowerInvariant());
        }
        return kolommen;
    }

    /// <summary>
    /// Kolomnamen uit de gegenereerde Postgres-DDL. <see cref="PostgresIdentifier"/> quote't
    /// onvoorwaardelijk, dus elke kolomregel begint met een gequote identifier — dat maakt de
    /// herkenning eenduidig, ook voor de gehyphende kolommen als "uitslag-regulier" (#890).
    /// </summary>
    private static HashSet<string> LeesPostgresKolommen(string ddl)
    {
        var kolommen = new HashSet<string>(StringComparer.Ordinal);
        var inBody = false;
        foreach (var regel in ddl.Split('\n'))
        {
            if (!inBody)
            {
                if (Regex.IsMatch(regel, @"CREATE\s+TABLE", RegexOptions.IgnoreCase)) inBody = true;
                continue;
            }
            if (Regex.IsMatch(regel, @"^\s*\)")) break;

            var m = Regex.Match(regel, "^\\s*\"([^\"]+)\"");
            if (m.Success) kolommen.Add(m.Groups[1].Value.ToLowerInvariant());
        }
        return kolommen;
    }

    private static string ZonderCommentaar(string regel)
    {
        var i = regel.IndexOf("--", StringComparison.Ordinal);
        return i >= 0 ? regel[..i] : regel;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("de testrunner moet ergens onder de repository-root draaien");
        return dir!.FullName;
    }
}
