using Npgsql;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/ClubScope.cs</c> (#888/#890).
/// <para>
/// <b><c>AddClubParam</c> toegevoegd</b> voor tabellen met <c>clubcode NOT NULL</c>
/// (<c>public.veldperiode</c>, <c>public.veldbeschikbaarheid</c>, <c>public.veldtraining</c>, en de
/// <c>clubcode</c>-uitvoerkolom van <c>planner.alle_wedstrijden_op_veld_ruw</c>) — de
/// beschikbaarheids- en bezettingsrepository heeft dit nodig, in tegenstelling tot de eerdere,
/// minimalere versie van deze klasse die alleen <c>his.*</c>-tabellen via <c>HisFilter</c>/
/// <c>AddHisParams</c> raakte. <c>LegacyFilter</c> (voor <c>avg.Teambegeleiding</c>) blijft nog
/// buiten scope — geen huidige aanroeper heeft hem nodig.
/// </para>
/// <para>
/// Twee predicaten, om dezelfde reden als het origineel: <c>planner.*</c>/<c>public.*</c> heeft
/// <c>clubcode NOT NULL</c> → strikt filteren; <c>his.*</c> heeft <c>clubcode</c> NULLABLE
/// (<c>Database.Postgres/EntityDefinition.cs</c>'s <c>ColumnDefinition</c> default) → rijen zonder
/// stempel horen bij de primaire club.
/// </para>
/// </summary>
internal static class PostgresClubScope
{
    internal const string ClubCodeParam = "@clubcode";
    internal const string PrimaryClubCodeParam = "@primaireclubcode";

    /// <summary>SQL-predicaat voor een <c>his.*</c>-tabel met NULL-tolerantie. Vereist beide
    /// parameters — zet ze via <see cref="AddHisParams"/>.</summary>
    internal static string HisFilter(string alias)
        => $"COALESCE({alias}.clubcode, {PrimaryClubCodeParam}) = {ClubCodeParam}";

    /// <summary>De primaire (syncenabled) club van deze deployment.</summary>
    internal static string Primary
    {
        get
        {
            var cc = PostgresAppSettings.GetSetting("clubCode");
            if (string.IsNullOrWhiteSpace(cc))
                throw new InvalidOperationException(
                    "Vereiste instelling 'clubcode' ontbreekt in public.appsettings");
            return cc;
        }
    }

    /// <summary>Geen expliciete waarde → de primaire club van deze deployment. Nooit een stille
    /// lege string: dat zou het filter effectief uitschakelen.</summary>
    internal static string Resolve(string? clubCode)
        => string.IsNullOrWhiteSpace(clubCode) ? Primary : clubCode;

    /// <summary>Zet <c>@clubcode</c> voor tabellen met <c>clubcode NOT NULL</c>.</summary>
    internal static string AddClubParam(NpgsqlCommand cmd, string? clubCode)
    {
        var cc = Resolve(clubCode);
        cmd.Parameters.AddWithValue("clubcode", cc);
        return cc;
    }

    /// <summary>Zet <c>@clubcode</c> en <c>@primaireclubcode</c> voor queries die <c>his.*</c> raken.</summary>
    internal static void AddHisParams(NpgsqlCommand cmd, string? clubCode)
    {
        AddClubParam(cmd, clubCode);
        cmd.Parameters.AddWithValue("primaireclubcode", Primary);
    }

    /// <summary>
    /// Leest de Accommodatie-instelling van de opgegeven club rechtstreeks uit public.appsettings
    /// — niet uit <see cref="PostgresAppSettings"/>'s cache, die uitsluitend de primaire
    /// (syncenabled) club bevat. Zelfde reden als het SQL Server-origineel (#694): een lookup voor
    /// ALLSTARS-demodata zou anders altijd de accommodatienaam van de primaire club terugkrijgen.
    /// </summary>
    internal static async Task<string> RequireAccommodatieAsync(NpgsqlConnection conn, string clubCode)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT accommodatie FROM public.appsettings WHERE clubcode = @clubcode LIMIT 1", conn);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        var result = await cmd.ExecuteScalarAsync();
        var waarde = result as string;
        if (string.IsNullOrWhiteSpace(waarde))
            throw new InvalidOperationException(
                $"Vereiste instelling 'accommodatie' ontbreekt in public.appsettings voor ClubCode '{clubCode}'");
        return waarde;
    }
}
