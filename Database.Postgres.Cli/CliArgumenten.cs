namespace Database.Postgres.Cli;

/// <summary>
/// De drie standen van de migratie-CLI, afgeleid uit <c>args</c>.
/// </summary>
/// <param name="EnsureHisTables">
/// <c>--ensure-his-tables</c> (#1060): maakt <c>his.teams</c>/<c>his.matches</c>/
/// <c>his.matchdetails</c> aan langs dezelfde weg als de ETL.
/// </param>
/// <param name="SeedDemodata"><c>--seed-demodata</c> (#1246).</param>
/// <param name="SeedScriptPad">
/// Het pad achter <c>--seed-demodata</c>, of <c>null</c> als dat er niet stond — dan valt de CLI
/// terug op het script in de repository.
/// </param>
/// <param name="MigratiePad">
/// Het eerste positionele argument, of <c>null</c> als dat er niet was.
/// </param>
public sealed record CliArgumenten(
    bool EnsureHisTables,
    bool SeedDemodata,
    string? SeedScriptPad,
    string? MigratiePad);

/// <summary>
/// Leest de argumenten van de migratie-CLI.
/// </summary>
/// <remarks>
/// Apart van <c>Program.cs</c> omdat dat bestand top-level statements gebruikt: die compileren
/// naar een onbereikbare <c>&lt;Main&gt;$</c>, dus de argumentafhandeling was niet los te bevragen
/// (#1302). En dat is precies de logica die bepaalt of <c>deploy.yml</c> migraties toepast of
/// demodata seedt — een verkeerde uitkomst daar is een verkeerde deploy.
/// </remarks>
public static class CliArgumentParser
{
    private const string VlagEnsureHisTables = "--ensure-his-tables";
    private const string VlagSeedDemodata = "--seed-demodata";

    public static CliArgumenten Lees(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var ensureHisTables = args.Contains(VlagEnsureHisTables, StringComparer.Ordinal);

        var seedIndex = Array.IndexOf(args, VlagSeedDemodata);
        var seedDemodata = seedIndex >= 0;

        // Het pad hoort direct achter de vlag te staan en mag zelf geen vlag zijn — anders zou
        // 'db-cli --seed-demodata --ensure-his-tables' de tweede vlag als bestandsnaam lezen.
        string? seedScriptPad = null;
        if (seedDemodata &&
            seedIndex + 1 < args.Length &&
            !IsVlag(args[seedIndex + 1]))
        {
            seedScriptPad = args[seedIndex + 1];
        }

        // Positioneel = alles wat geen vlag is. Het pad achter --seed-demodata telt daar niet mee:
        // dat hoort bij die vlag, niet bij de migratiemap.
        string? migratiePad = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (IsVlag(args[i])) continue;
            if (seedScriptPad is not null && i == seedIndex + 1) continue;
            migratiePad = args[i];
            break;
        }

        return new CliArgumenten(ensureHisTables, seedDemodata, seedScriptPad, migratiePad);
    }

    private static bool IsVlag(string argument) => argument.StartsWith("--", StringComparison.Ordinal);
}
