using Database.Postgres.Tests;
using AwesomeAssertions;
using FunctionApp.Postgres.Planner;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Regressietests voor #1331 op <see cref="PlannerSettingsRepository.GetVeldenAsync"/>: (a) een
/// alfabetische <c>veldnaam</c> (A, B, C) is een even geldige vrije clubconfiguratie als een
/// numerieke ("Veld 1") en mag nooit de volgorde bepalen — dat blijft <c>veldnummer</c> — en (b) de
/// bestaande ClubCode-scope blijft afgedekt: velden van een andere club mogen nooit meelekken.
/// Beide waren tot #1331 ongetest (Codex-review vond geen bestaande dekking).
/// <para>
/// <b>Veldnummers 133101-133112</b> — <c>public.velden.veldnummer</c> is een kale PK zonder
/// ClubCode-scope (migratie 001; zie ook de klasse-doc-comments van
/// <see cref="PlannerMatchOpponentLookupIntegrationTests"/> en
/// <see cref="PlannerMatchSearchRepositoryIntegrationTests"/> over dezelfde valkuil). Een uniek,
/// issue-gebonden nummerbereik voorkomt botsing met de veldnummers van andere testklassen.
/// </para>
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class PlannerSettingsRepositoryVeldenIntegrationTests
{
    private const string ClubA = "testclub-1331a";
    private const string ClubB = "testclub-1331b";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task GetVeldenAsync_AlfabetischeVeldnamenSorterenOpVeldnummerNietOpNaam()
    {
        await SchoonAsync();
        try
        {
            // Bewust in omgekeerde volgorde ingevoegd: als de repository (of een aanroeper) ooit
            // stiekem op veldnaam of invoegvolgorde zou sorteren i.p.v. op veldnummer, zou "C" hier
            // vóór "A" komen te staan en maakt deze test dat zichtbaar.
            await ZetVeldAsync(133103, "C", ClubA);
            await ZetVeldAsync(133101, "A", ClubA);
            await ZetVeldAsync(133102, "B", ClubA);

            var velden = await PlannerSettingsRepository.GetVeldenAsync(ConnectionString, ClubA);

            velden.Select(v => v.VeldNummer).Should().Equal(133101, 133102, 133103);
            velden.Select(v => v.VeldNaam).Should().Equal(
                new[] { "A", "B", "C" },
                "de volgorde hoort uit veldnummer te komen — de alfabetische displaynaam is vrije " +
                "clubconfiguratie, geen sorteersleutel");
        }
        finally { await SchoonAsync(); }
    }

    [PostgresFact]
    public async Task GetVeldenAsync_ToontNooitVeldenVanEenAndereClub()
    {
        await SchoonAsync();
        try
        {
            await ZetVeldAsync(133101, "A", ClubA);
            await ZetVeldAsync(133111, "Geheim veld van club B", ClubB);
            await ZetVeldAsync(133112, "Nog een geheim veld van club B", ClubB);

            var velden = await PlannerSettingsRepository.GetVeldenAsync(ConnectionString, ClubA);

            velden.Should().ContainSingle();
            velden.Should().OnlyContain(v => v.VeldNummer == 133101 && v.VeldNaam == "A");
        }
        finally { await SchoonAsync(); }
    }

    private static async Task SchoonAsync() =>
        await ExecAsync("DELETE FROM public.velden WHERE clubcode IN (@a, @b)",
            ("a", ClubA), ("b", ClubB));

    private static async Task ZetVeldAsync(int veldnummer, string veldnaam, string clubCode) =>
        await ExecAsync(
            "INSERT INTO public.velden (veldnummer, veldnaam, actief, clubcode) VALUES (@nr, @naam, true, @club)",
            ("nr", veldnummer), ("naam", veldnaam), ("club", clubCode));

    private static async Task ExecAsync(string sql, params (string Naam, object Waarde)[] parameters)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde);
        await cmd.ExecuteNonQueryAsync();
    }
}
