using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Planner;

/// <summary>
/// Centrale ClubCode-scope voor alle planner- en bezettingsqueries (#573, #580).
///
/// Elke query die clubdata leest is hard gescoped op ClubCode. Zonder filter kan
/// demodata (ALLSTARS) of data van een andere club in zoekresultaten én in
/// beslislogica belanden — dat is zowel een planningsfout als een data-isolatieprobleem.
///
/// Twee predicaten, omdat de tabellen verschillen:
///   • <c>planner.*</c> en <c>dbo.*</c> — <c>[ClubCode]</c> is NOT NULL → strikt filteren.
///   • <c>his.*</c> — <c>[ClubCode]</c> is NULLABLE (toegevoegd via migratie 001).
///     Rijen zonder stempel horen bij de primaire club; dat is exact wat migratie 001
///     doet met de backfill. Zonder die NULL-tolerantie zouden nog niet gestempelde
///     wedstrijden uit de bezetting vallen → onderschatte bezetting → dubbele boekingen.
///     ALLSTARS-rijen zijn altijd expliciet gestempeld en lekken dus nooit mee.
/// </summary>
internal static class ClubScope
{
    internal const string ClubCodeParam        = "@clubCode";
    internal const string PrimaryClubCodeParam = "@primaireClubCode";

    /// <summary>
    /// SQL-predicaat voor een <c>his.*</c>-tabel met NULL-tolerantie.
    /// Gebruik de tabel-alias, bijvoorbeeld <c>HisFilter("m")</c>.
    /// Vereist beide parameters — zet ze via <see cref="AddHisParams"/>.
    /// </summary>
    internal static string HisFilter(string alias)
        => $"ISNULL({alias}.[ClubCode], {PrimaryClubCodeParam}) = {ClubCodeParam}";

    /// <summary>De primaire (SyncEnabled) club van deze deployment.</summary>
    internal static string Primary
    {
        get
        {
            var cc = SystemUtilities.AppSettings.GetSetting("clubCode");
            if (string.IsNullOrWhiteSpace(cc))
                throw new InvalidOperationException(
                    "Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");
            return cc;
        }
    }

    /// <summary>
    /// Lost de effectieve ClubCode op. Geen expliciete waarde (e-mailflow, timer-triggers)
    /// → de primaire club van deze deployment. Nooit een stille lege string: dat zou het
    /// filter effectief uitschakelen.
    /// </summary>
    internal static string Resolve(string? clubCode)
        => string.IsNullOrWhiteSpace(clubCode) ? Primary : clubCode;

    /// <summary>Zet <c>@clubCode</c> voor tabellen met NOT NULL ClubCode.</summary>
    internal static string AddClubParam(SqlCommand cmd, string? clubCode)
    {
        var cc = Resolve(clubCode);
        cmd.Parameters.AddWithValue(ClubCodeParam, cc);
        return cc;
    }

    /// <summary>Zet <c>@clubCode</c> én <c>@primaireClubCode</c> voor queries die <c>his.*</c> raken.</summary>
    internal static string AddHisParams(SqlCommand cmd, string? clubCode)
    {
        var cc = AddClubParam(cmd, clubCode);
        cmd.Parameters.AddWithValue(PrimaryClubCodeParam, Primary);
        return cc;
    }
}
