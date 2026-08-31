using Database.Postgres.Tests;
using FluentAssertions;
using FunctionApp.Postgres.Email;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Legt het gedrag van <see cref="EmailTemplateService"/> op de Postgres-tier vast (#889).
///
/// <para>
/// De cache is <b>statisch en procesbreed</b>, dus elke test begint met
/// <see cref="EmailTemplateService.InvalidateCache"/> — anders zou de uitkomst afhangen van de
/// volgorde waarin de tests draaien. Dat de cache blijft plakken is precies wat twee van deze
/// tests moeten aantonen, dus hem uitschakelen is geen optie.
/// </para>
///
/// <para>Zie <see cref="PostgresSyncFixtureIntegrationTests"/> voor de lokale containeropzet.</para>
/// </summary>
public class EmailTemplateServiceIntegrationTests
{
    private const string Club = "testclub-tpl";
    private const string AndereClub = "testclub-tpl2";
    private const string Key = "verzetverzoek-bevestiging";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task GetTemplateAsync_ActiefSjabloon_WordtGevondenMetOnderwerpEnBody()
    {
        await SchoonAsync();
        await SjabloonAsync(Club, Key, "Bevestiging", "Hallo {{team}}, akkoord.", actief: true);

        var template = await EmailTemplateService.GetTemplateAsync(ConnectionString, Key, Club);

        template.Should().NotBeNull();
        template!.Key.Should().Be(Key);
        template.Onderwerp.Should().Be("Bevestiging");
        template.Body.Should().Be("Hallo {{team}}, akkoord.");
    }

    /// <summary>
    /// Een inactief sjabloon moet <c>null</c> opleveren, niet de rij zelf: de aanroeper valt dan
    /// terug op de hardcoded default. Zou het `actief`-filter wegvallen, dan zou een bewust
    /// uitgezet sjabloon alsnog verstuurd worden.
    /// </summary>
    [PostgresFact]
    public async Task GetTemplateAsync_InactiefSjabloon_LevertNull()
    {
        await SchoonAsync();
        await SjabloonAsync(Club, Key, "Uitgezet", "Mag niet gebruikt worden.", actief: false);

        (await EmailTemplateService.GetTemplateAsync(ConnectionString, Key, Club)).Should().BeNull();
    }

    [PostgresFact]
    public async Task GetTemplateAsync_OnbekendeSleutelOfLegeSleutel_LevertNull()
    {
        await SchoonAsync();
        await SjabloonAsync(Club, Key, "Bevestiging", "Body", actief: true);

        (await EmailTemplateService.GetTemplateAsync(ConnectionString, "bestaat-niet", Club)).Should().BeNull();
        (await EmailTemplateService.GetTemplateAsync(ConnectionString, "", Club)).Should().BeNull();
    }

    /// <summary>
    /// #706: de cachesleutel is (club, key), niet alleen key. Met alleen de key zou de tweede club
    /// het sjabloon van de eerste krijgen die het ophaalde — gegevens van een andere club in haar
    /// eigen antwoord. Beide clubs hebben hier bewust hetzelfde sjabloonsleutelwoord.
    /// </summary>
    [PostgresFact]
    public async Task GetTemplateAsync_TweeClubsZelfdeSleutel_KrijgenElkHunEigenSjabloon()
    {
        await SchoonAsync();
        await SjabloonAsync(Club, Key, "Onderwerp A", "Body van club A", actief: true);
        await SjabloonAsync(AndereClub, Key, "Onderwerp B", "Body van club B", actief: true);

        // Volgorde is relevant: de eerste ophaalactie vult de cache.
        var eerste = await EmailTemplateService.GetTemplateAsync(ConnectionString, Key, Club);
        var tweede = await EmailTemplateService.GetTemplateAsync(ConnectionString, Key, AndereClub);

        eerste!.Body.Should().Be("Body van club A");
        tweede!.Body.Should().Be("Body van club B",
            "de cachesleutel bevat de clubcode; anders zou club B het sjabloon van club A terugkrijgen (#706)");
    }

    /// <summary>
    /// Twee dingen tegelijk, en bewust in één test omdat ze elkaars bewijs zijn: de cache houdt een
    /// waarde vast (anders zou hij niets doen) én <see cref="EmailTemplateService.InvalidateCache"/>
    /// laat hem los (anders zou een admin-wijziging vijf minuten niet zichtbaar zijn — precies
    /// waarvoor `AdminTemplatesFunction` die aanroep doet).
    /// </summary>
    [PostgresFact]
    public async Task Cache_HoudtWaardeVastTotInvalidateCache()
    {
        await SchoonAsync();
        await SjabloonAsync(Club, Key, "Origineel", "Originele body", actief: true);

        (await EmailTemplateService.GetTemplateAsync(ConnectionString, Key, Club))!.Body
            .Should().Be("Originele body");

        // Wijziging buiten de service om: de cache mag die nog niet zien.
        await ExecAsync(
            "UPDATE public.emailtemplateinstellingen SET bodytemplate = 'Gewijzigde body' " +
            "WHERE clubcode = @club AND templatekey = @key",
            ("club", Club), ("key", Key));

        (await EmailTemplateService.GetTemplateAsync(ConnectionString, Key, Club))!.Body
            .Should().Be("Originele body", "de cache is nog geldig (TTL 5 minuten)");

        EmailTemplateService.InvalidateCache();

        (await EmailTemplateService.GetTemplateAsync(ConnectionString, Key, Club))!.Body
            .Should().Be("Gewijzigde body", "na InvalidateCache moet de database opnieuw gelezen worden");
    }

    [PostgresFact]
    public void ApplyPlaceholders_VervangtHoofdletterongevoeligEnLaatOnbekendeStaan()
    {
        EmailTemplateService.ApplyPlaceholders(
                "Hallo {{Team}}, om {{tijd}} op {{onbekend}}.",
                new Dictionary<string, string> { ["team"] = "JO13-1", ["TIJD"] = "14:30" })
            .Should().Be("Hallo JO13-1, om 14:30 op {{onbekend}}.");
    }

    private static async Task SchoonAsync()
    {
        EmailTemplateService.InvalidateCache();
        await ExecAsync(
            "DELETE FROM public.emailtemplateinstellingen WHERE clubcode = ANY(@clubs)",
            ("clubs", new[] { Club, AndereClub }));
    }

    private static async Task SjabloonAsync(string club, string key, string onderwerp, string body, bool actief) =>
        await ExecAsync(@"
            INSERT INTO public.emailtemplateinstellingen (templatekey, onderwerp, bodytemplate, actief, clubcode)
            VALUES (@key, @onderwerp, @body, @actief, @club)
            ON CONFLICT (templatekey, clubcode) DO UPDATE
                SET onderwerp = EXCLUDED.onderwerp, bodytemplate = EXCLUDED.bodytemplate,
                    actief = EXCLUDED.actief, mta_modified = NOW()",
            ("key", key), ("onderwerp", onderwerp), ("body", body), ("actief", actief), ("club", club));

    private static async Task ExecAsync(string sql, params (string Naam, object Waarde)[] parameters)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde);
        await cmd.ExecuteNonQueryAsync();
    }
}
