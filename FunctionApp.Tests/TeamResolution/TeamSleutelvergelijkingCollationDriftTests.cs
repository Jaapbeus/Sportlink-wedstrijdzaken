using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

/// <summary>
/// Aparte, generieke drift-test voor #1294 — de tegenhanger van
/// <see cref="TeamCandidateRepositoryCollationTests"/>, maar over meerdere bestanden.
/// <para>
/// Die test controleert exacte patronen (kolomnaam + specifieke parameternaam) in precies
/// <c>TeamCandidateRepository.cs</c>. #1294 voegde dezelfde drie sleutelkolommen toe in drie andere
/// bestanden, elk met een eigen parameternaam (<c>@sleutel</c>, <c>@ruweTekst</c>,
/// <c>@genormaliseerd</c>) — een test die daar per bestand een eigen <c>[InlineData]</c>-regel voor
/// zou krijgen, dupliceert de kolomlijst een derde keer (naast <see cref="TabelPerKolom"/> in
/// <c>TeamCandidateIndexSargabilityTests</c>). In plaats daarvan werkt deze test kolomgericht en
/// parameternaam-onafhankelijk: hij verwijdert eerst elke correct in <c>UPPER(...)</c> gewrapte
/// vergelijking uit de bestandsinhoud, en eist dat er dan geen kale <c>[Kolom] = @...</c>-vorm van
/// diezelfde kolom overblijft — ongeacht welke parameternaam erbij hoort.
/// </para>
/// <para>
/// Bewust tekstueel: draait zonder database, dus faalt vóór een merge.
/// </para>
/// </summary>
public class TeamSleutelvergelijkingCollationDriftTests
{
    private static readonly string[] Kolommen =
    [
        "TeamnaamGenormaliseerd",
        "RuweTekst",
        "RuweTekstGenormaliseerd",
    ];

    private static readonly string[] BronBestanden =
    [
        "FunctionApp/TeamResolution/TeamCandidateRepository.cs",
        "FunctionApp/Planner/Repositories/PlannerMatchRepository.cs",
        "FunctionApp/TeamResolution/TeamAliasLearningService.cs",
        "FunctionApp/TeamResolution/TeamCanonicalisatieService.cs",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sportlink-wedstrijdzaken.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("de testrunner moet ergens onder de repository-root draaien");
        return dir!.FullName;
    }

    private static string Lees(string relatiefPad) => File.ReadAllText(Path.Combine(RepoRoot(), relatiefPad));

    /// <summary>Filtert doc-comments weg — die citeren het verboden patroon soms letterlijk ter illustratie.</summary>
    private static string ZonderCommentaar(string inhoud)
        => string.Join('\n', inhoud.Split('\n').Where(r => !r.TrimStart().StartsWith("///")));

    public static IEnumerable<object[]> BestandKruisKolom()
    {
        foreach (var bestand in BronBestanden)
            foreach (var kolom in Kolommen)
                yield return [bestand, kolom];
    }

    [Theory]
    [MemberData(nameof(BestandKruisKolom))]
    public void GeenKaleVergelijkingZonderUpper(string bestand, string kolom)
    {
        var inhoud = ZonderCommentaar(Lees(bestand));

        // Eerst elke correcte UPPER(...)=UPPER(@...)-vorm van deze kolom verwijderen — met of
        // zonder table-alias ervoor (bijv. "a.[RuweTekst]" of kaal "[TeamnaamGenormaliseerd]").
        var zonderGoedeVorm = Regex.Replace(
            inhoud,
            $@"UPPER\((?:\w+\.)?\[{kolom}\]\)\s*=\s*UPPER\(@\w+\)",
            string.Empty);

        // Wat overblijft mag deze kolom niet meer kaal tegen een parameter vergelijken — maar
        // alleen als *vergelijking* (WHERE/AND/OR), niet als UPDATE-toewijzing: "SET [Kolom] =
        // @param" heeft dezelfde tekstvorm als een vergelijking maar is een schrijfactie, en
        // UPPER() daarop zou de opgeslagen waarde corrumperen in plaats van hem te lezen.
        var kaleVorm = $@"\b(?:WHERE|AND|OR)\b\s*\(?\s*(?:\w+\.)?\[{kolom}\]\s*=\s*@\w+";
        Regex.IsMatch(zonderGoedeVorm, kaleVorm, RegexOptions.IgnoreCase).Should().BeFalse(
            $"{bestand} vergelijkt [{kolom}] nog kaal (zonder UPPER) tegen een parameter — dat leunt " +
            "stilzwijgend op database-collatie en gedraagt zich anders op een tier met een " +
            "case-sensitive default-collatie (#1294, #820)");
    }
}
