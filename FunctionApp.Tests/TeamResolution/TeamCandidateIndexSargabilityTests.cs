using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

/// <summary>
/// Bewaakt de keerzijde van <see cref="TeamCandidateRepositoryCollationTests"/> (#1232, #1280).
/// Die test eist dat elke sleutelvergelijking in <c>TeamCandidateRepository.cs</c> expliciet in
/// <c>UPPER(...)</c> staat (#820). Deze test eist dat er dan ook een index bestaat die zo'n
/// predicaat kán bedienen.
/// <para>
/// Waarom dat niet vanzelf spreekt: SQL Server verwijdert een overbodige <c>UPPER()</c> niet, ook
/// niet onder de case-insensitieve modelcollatie (<c>1033, CI</c>). Een index op de kále kolom wordt
/// dan alleen nog als residueel predicaat gebruikt — of helemaal niet. Gemeten op 200.000 rijen:
/// 3181 logische leesbewerkingen tegenover 6. Dat verschil is per definitie onzichtbaar voor "de
/// query werkt" en "de build is groen", en dat is precies waarom het ruim een jaar bleef staan.
/// </para>
/// <para>
/// Bewust tekstueel, net als de collatietest: draait zonder database, dus faalt vóór een merge.
/// </para>
/// </summary>
public class TeamCandidateIndexSargabilityTests
{
    /// <summary>Welke tabel hoort bij welke sleutelkolom uit de repository-query's.</summary>
    private static readonly Dictionary<string, string> TabelPerKolom = new()
    {
        ["TeamnaamGenormaliseerd"] = "Teams",
        ["RuweTekst"] = "TeamAliassen",
        ["RuweTekstGenormaliseerd"] = "TeamAliassen",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("de testrunner moet ergens onder de repository-root draaien");
        return dir!.FullName;
    }

    private static string Lees(string relatiefPad) => File.ReadAllText(Path.Combine(RepoRoot(), relatiefPad));

    private static string ZonderCommentaar(string inhoud)
        => string.Join('\n', inhoud.Split('\n').Where(r => !r.TrimStart().StartsWith("///")));

    /// <summary>
    /// Leest uit de repository welke kolommen daadwerkelijk via <c>UPPER()</c> worden vergeleken.
    /// Zo groeit deze test automatisch mee met een nieuwe sleutelvergelijking in plaats van een
    /// tweede, handmatig bijgehouden lijst te worden.
    /// </summary>
    private static IReadOnlyList<string> UpperVergelekenKolommen()
    {
        var inhoud = ZonderCommentaar(Lees("FunctionApp/TeamResolution/TeamCandidateRepository.cs"));
        return Regex.Matches(inhoud, @"UPPER\((?:\w+\.)?\[(?<kolom>\w+)\]\)\s*=\s*UPPER\(@")
                    .Select(m => m.Groups["kolom"].Value)
                    .Distinct()
                    .ToList();
    }

    [Fact]
    public void ElkeUpperVergelekenKolomIsBekend()
    {
        var kolommen = UpperVergelekenKolommen();
        kolommen.Should().NotBeEmpty("zonder treffers zou deze test stilzwijgend niets bewaken");

        foreach (var kolom in kolommen)
            TabelPerKolom.Should().ContainKey(kolom,
                $"'{kolom}' wordt via UPPER() vergeleken maar staat niet in TabelPerKolom — vul de tabel aan " +
                "zodat de indexcontrole hieronder ook voor deze kolom geldt (#1280)");
    }

    /// <summary>
    /// De SSDT-definitie moet voor elke UPPER()-vergeleken kolom een persisted computed column én
    /// een index daarop bevatten. SQL Server matcht de expressie <c>UPPER(kolom)</c> uit de query
    /// automatisch tegen zo'n kolom; dat is de tegenhanger van Postgres' expressie-index.
    /// </summary>
    [Fact]
    public void SsdtDefinitieHeeftPersistedComputedColumnEnIndexPerSleutelkolom()
    {
        foreach (var kolom in UpperVergelekenKolommen())
        {
            var tabel = TabelPerKolom[kolom];
            var definitie = Lees($"Database/dbo/Tables/{tabel}.sql");

            Regex.IsMatch(definitie, $@"\[{kolom}Upper\]\s+AS\s+UPPER\(\[{kolom}\]\)\s+PERSISTED")
                .Should().BeTrue(
                    $"dbo.{tabel} mist de persisted computed column [{kolom}Upper] — zonder die kolom kan geen " +
                    $"index het predicaat UPPER([{kolom}]) = UPPER(@p) bedienen (#1280)");

            Regex.IsMatch(definitie, $@"CREATE\s+(UNIQUE\s+)?NONCLUSTERED\s+INDEX[^;]*\[{kolom}Upper\]")
                .Should().BeTrue(
                    $"dbo.{tabel}.[{kolom}Upper] bestaat maar wordt niet geïndexeerd — een computed column " +
                    "zonder index lost de non-sargability niet op (#1280)");
        }
    }

    /// <summary>
    /// Dezelfde objecten moeten in het PostDeployment-script staan. De deploy publiceert geen
    /// dacpac (zie de kop van dat script): alleen dit bestand draait tegen de database, dus een
    /// object dat er niet in staat bestaat in productie niet — dezelfde klasse als #595/#734.
    /// </summary>
    [Fact]
    public void PostDeploymentMaaktDezelfdeObjectenAan()
    {
        var postDeploy = Lees("Database/Script.PostDeployment1.sql");

        foreach (var kolom in UpperVergelekenKolommen())
        {
            postDeploy.Should().MatchRegex($@"\[{kolom}Upper\]\s+AS\s+UPPER\(\[{kolom}\]\)\s+PERSISTED",
                $"Script.PostDeployment1.sql maakt [{kolom}Upper] niet aan — op een bestaande database " +
                "ontbreekt de kolom dan (#1280)");

            postDeploy.Should().MatchRegex($@"CREATE\s+(UNIQUE\s+)?NONCLUSTERED\s+INDEX[^;]*\[{kolom}Upper\]",
                $"Script.PostDeployment1.sql maakt geen index op [{kolom}Upper] aan (#1280)");
        }
    }

    /// <summary>
    /// Een persisted computed column aanmaken of indexeren faalt met Msg 1934 zodra
    /// <c>QUOTED_IDENTIFIER</c> uit staat — en sqlcmd, waarmee zowel de CI-job als de deploy dit
    /// script draait, zet hem standaard OFF. Deze regel is dus geen netheid maar een harde
    /// voorwaarde; hem weghalen breekt de deploy op een manier die lokaal in SSMS nooit optreedt.
    /// </summary>
    [Fact]
    public void PostDeploymentZetQuotedIdentifierAanVoorDeComputedColumns()
    {
        var postDeploy = Lees("Database/Script.PostDeployment1.sql");

        // Regelanker, geen IndexOf: de toelichting boven het blok noemt deze SET letterlijk, en een
        // losse tekstzoektocht vindt dan het commentaar in plaats van het statement — een groene
        // test die niets bewaakt. (Vastgesteld met een negatieve test tijdens #1280.)
        var setStatement = Regex.Match(postDeploy, @"^SET\s+QUOTED_IDENTIFIER\s+ON\s*;", RegexOptions.Multiline);
        setStatement.Success.Should().BeTrue(
            "zonder dit statement faalt elke computed-column-DDL onder sqlcmd (Msg 1934)");
        var setIndex = setStatement.Index;

        var eersteComputed = Regex.Match(postDeploy, @"AS\s+UPPER\(\[\w+\]\)\s+PERSISTED");
        eersteComputed.Success.Should().BeTrue();
        setIndex.Should().BeLessThan(eersteComputed.Index,
            "SET QUOTED_IDENTIFIER ON moet vóór de eerste computed-column-DDL staan");
    }
}
