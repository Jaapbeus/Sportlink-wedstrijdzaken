using System.Text.RegularExpressions;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Source-level guard for literal GetSetting calls in both built tiers. Dynamic keys cannot be
/// determined statically; the only current dynamic call delegates a key to RequireSetting.
/// </summary>
public class AppSettingsLoaderCoverageTests
{
    private static readonly Regex Getter = new(@"\bGetSetting\s*\(\s*""(?<key>\w+)""\s*\)");
    private static readonly Regex Assignment = new(@"\b(?:settings|nieuw)\s*\[\s*""(?<key>\w+)""\s*\]\s*(?:=|,)");
    private static readonly Regex Initializer = new(@"\[\s*""(?<key>\w+)""\s*\]\s*=");
    private static readonly Regex SqlSet = new(@"\bSet\s*\(\s*""(?<key>\w+)""\s*,");

    [Theory]
    [InlineData("FunctionApp", "FunctionApp/Utilities.cs")]
    [InlineData("FunctionApp.Postgres", "FunctionApp.Postgres/PostgresAppSettings.cs")]
    public void LiteralGetSettingKeysAreLoaded(string tier, string loaderPath)
    {
        var root = RepositoryRoot();
        var requested = Directory.EnumerateFiles(Path.Combine(root, tier), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Intersect(["bin", "obj"]).Any())
            .SelectMany(path => Keys(Getter, File.ReadAllText(path)))
            .ToHashSet(StringComparer.Ordinal);

        var loader = File.ReadAllText(Path.Combine(root, loaderPath));
        var loaded = Keys(Assignment, loader).Concat(Keys(Initializer, loader)).Concat(Keys(SqlSet, loader))
            .ToHashSet(StringComparer.Ordinal);

        // #1821: this nullable first-team name has no column in either built tier. The SQL
        // planner's legacy read always returns null; the Postgres planner passes null directly.
        // Keep this exception narrow until the SQL caller is removed.
        if (tier == "FunctionApp")
        {
            Assert.Contains("eersteElftalNaam", requested);
            Assert.DoesNotContain("eersteElftalNaam", loaded);
            requested.Remove("eersteElftalNaam");
        }

        Assert.Empty(Missing(requested, loaded));
    }

    [Fact]
    public void GuardDetectsEachMissingEmailMapping()
    {
        var root = RepositoryRoot();
        var requested = Directory.EnumerateFiles(Path.Combine(root, "FunctionApp.Postgres"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Intersect(["bin", "obj"]).Any())
            .SelectMany(path => Keys(Getter, File.ReadAllText(path)))
            .ToHashSet(StringComparer.Ordinal);
        var loader = File.ReadAllText(Path.Combine(root, "FunctionApp.Postgres/PostgresAppSettings.cs"));
        var loaded = Keys(Assignment, loader).Concat(Keys(Initializer, loader))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in new[] { "emailVoetnoot", "coordinatorNaam", "coordinatorFunctie", "plannerEmailAdres", "herplanDeadlineDagen" })
        {
            Assert.Contains(key, requested);
            Assert.Contains(key, loaded);
            var incomplete = new HashSet<string>(loaded, StringComparer.Ordinal);
            incomplete.Remove(key);
            Assert.Contains(key, Missing(requested, incomplete));
        }
    }

    private static IEnumerable<string> Keys(Regex pattern, string source)
        => pattern.Matches(source).Select(match => match.Groups["key"].Value);

    private static string[] Missing(HashSet<string> requested, HashSet<string> loaded)
        => requested.Except(loaded, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "FunctionApp.Postgres.Tests")))
                return dir.FullName;

        throw new DirectoryNotFoundException("Could not locate repository root for settings-loader guard.");
    }
}
