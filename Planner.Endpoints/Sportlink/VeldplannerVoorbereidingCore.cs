using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;
using Planner.Shared.Planning;

namespace Planner.Endpoints.Sportlink;

/// <summary>De uitkomst van één voorbereidingsrun: alleen aantallen, nooit namen, URL's of tokens.</summary>
public sealed record VeldplannerVoorbereidingResultaat(
    int Datums, int Mislukt, int Toegevoegd, int Gewijzigd, int Verwijderd, int NummersOpgevraagd, int NummersOnbekend);

/// <summary>
/// Legt de veldplanner-blokken en hun bondswedstrijdnummers vooraf vast (#1917), zodat de Planning ze al in de tabel vindt en de
/// eerste bezoeker van een nieuwe datum niet op ±40 opvragen bij Sportlink hoeft te wachten (gemeten: 15,3 s tegen 2,8 s).
/// <para>
/// De gedeelde orkestratie van beide tiers: de tier-eigen timer geeft de client, de opslag en de datums met eigen wedstrijden
/// mee. Het werk zelf is hetzelfde als bij een planningsaanvraag (<see cref="VeldplannerOverlayCore.HaalBlokkenAsync"/> en
/// <see cref="VeldplannerBlokSynchronisatie"/>): vergelijken op sleutel en hash, alleen het verschil schrijven, een bekend
/// wedstrijdnummer nooit opnieuw opvragen. Een run zonder wijzigingen kost dus per datum één veldplanner-aanroep.
/// </para>
/// </summary>
public static class VeldplannerVoorbereidingCore
{
    /// <summary>Na zoveel opeenvolgende mislukte datums stopt de run: een verlopen koppeling of storing bij Sportlink verdient niet nog tien aanroepen.</summary>
    public const int MaxOpeenvolgendMislukt = 3;

    /// <summary>
    /// De functienaam van de timer (uitschakelen via <c>AzureWebJobs.&lt;naam&gt;.Disabled</c>). De tier-bindingen schrijven hem als letterlijke tekst in
    /// <c>[Function("...")]</c>, want de tier-pariteitsguard (<c>check-tier-pariteit.sh</c>) leest functienamen letterlijk en bewaakt zo ook dat beide tiers dezelfde naam hebben.
    /// </summary>
    public const string FunctieNaam = "SportlinkVeldplannerVoorbereiding";

    /// <summary>
    /// 06:30 en 18:30 UTC: twee keer per dag, bewust niet vaker (issue #1917, kosten in GB-seconden), en buiten de sync
    /// (<c>0 0 8-20/2</c>) en de PublicMatchId-warmup (06:00).
    /// </summary>
    public const string Schema = "0 30 6,18 * * *";

    /// <summary>
    /// De volledige timerstroom van beide tiers: client controleren, datums zoeken in het venster van
    /// <see cref="SportlinkEndpointCore.VeldplannerVoorbereidDagen"/> dagen, en voorbereiden. De tier levert alleen de query, de opslag
    /// en de aansluiting.
    /// </summary>
    /// <param name="client">De uitkomst van <c>ClientVoorTimer</c>: <c>null</c> als de extensie uit staat, EgressGuard dicht is of de client ontbreekt.</param>
    /// <param name="clubCode">Lui geëvalueerd, pas na de clientcontrole: een ontbrekende instelling mag een uitgeschakelde timer niet laten falen.</param>
    /// <param name="zoekDatums">Geeft de datums met eigen wedstrijden op de accommodatie voor (clubcode, van, tot).</param>
    public static async Task VoerTimerUitAsync(
        ISportlinkClubClient? client, Func<string> clubCode, string rolNaam, string? accommodatie,
        Func<string, DateOnly, DateOnly, Task<IReadOnlyList<DateOnly>>> zoekDatums, IVeldplannerBlokStore store, ILogger log)
    {
        if (client == null) return;

        var club = clubCode();
        var vandaag = DateOnly.FromDateTime(DateTime.UtcNow);
        IReadOnlyList<DateOnly> datums;
        try
        {
            datums = await zoekDatums(club, vandaag, vandaag.AddDays(SportlinkEndpointCore.VeldplannerVoorbereidDagen));
        }
        catch (Exception ex)
        {
            log.LogError("Datums met wedstrijden ophalen mislukt ({ErrorType}) — veldplanner voorbereiden overgeslagen.", ex.GetType().Name);
            return;
        }

        await VoerUitAsync(client, store, club, rolNaam, accommodatie, datums, log);
    }

    /// <param name="datums">De datums met eigen wedstrijden op de accommodatie; de aanroeper bepaalt het venster.</param>
    /// <param name="vandaag">Alleen voor tests; standaard de UTC-datum van vandaag (bepaalt hoe oud bewaarde rijen mogen zijn).</param>
    public static async Task<VeldplannerVoorbereidingResultaat> VoerUitAsync(
        ISportlinkClubClient client, IVeldplannerBlokStore store, string clubCode, string rolNaam, string? accommodatie,
        IReadOnlyList<DateOnly> datums, ILogger log, DateOnly? vandaag = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(datums);

        // De democlub staat niet in Sportlink en mag er nooit om vragen (zelfde regel als de Planning).
        if (clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase) || datums.Count == 0)
            return new VeldplannerVoorbereidingResultaat(0, 0, 0, 0, 0, 0, 0);

        var dag = vandaag ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var opvrager = VeldplannerOverlayCore.MaakExternIdOpvrager(client, rolNaam);
        int mislukt = 0, toegevoegd = 0, gewijzigd = 0, verwijderd = 0, opgevraagd = 0, onbekend = 0, opeenvolgend = 0, verwerkt = 0;

        foreach (var datum in datums)
        {
            if (opeenvolgend >= MaxOpeenvolgendMislukt)
            {
                log.LogWarning("Veldplanner voorbereiden gestopt na {Aantal} opeenvolgende mislukte datums; de rest volgt bij de volgende run", opeenvolgend);
                break;
            }

            verwerkt++;
            try
            {
                // Zelfde toegangspad als de Planning; de aanroeper heeft toggle en EgressGuard al gecontroleerd (ClientVoorTimer).
                var opgehaald = await VeldplannerOverlayCore.HaalBlokkenAsync(
                    () => SportlinkTimerStatus.MagDraaien, () => (client, null), rolNaam, accommodatie, datum, log);
                if (opgehaald.Blokken == null)
                {
                    mislukt++; opeenvolgend++;
                    continue;
                }

                var r = await VeldplannerBlokSynchronisatie.SynchroniseerAsync(store, opvrager, clubCode, datum, opgehaald.Blokken, dag);
                toegevoegd += r.Toegevoegd; gewijzigd += r.Gewijzigd; verwijderd += r.Verwijderd;
                opgevraagd += r.ExternIdOpgelost; onbekend += r.ExternIdOnbekend;
                opeenvolgend = 0;
            }
            catch (Exception ex)
            {
                // Eén datum mag de andere nooit stoppen; alleen het type, nooit de inhoud (kan een URL of waarde bevatten).
                mislukt++; opeenvolgend++;
                log.LogWarning("Veldplanner voorbereiden voor {Datum} mislukt ({ErrorType})", datum, ex.GetType().Name);
            }
        }

        log.LogInformation(
            "Veldplanner voorbereid: {Datums} datums verwerkt, {Mislukt} mislukt; {Toegevoegd} toegevoegd, {Gewijzigd} gewijzigd, {Verwijderd} verwijderd; wedstrijdnummer {Opgevraagd} opgevraagd, {Onbekend} nog onbekend",
            verwerkt, mislukt, toegevoegd, gewijzigd, verwijderd, opgevraagd, onbekend);
        return new VeldplannerVoorbereidingResultaat(verwerkt, mislukt, toegevoegd, gewijzigd, verwijderd, opgevraagd, onbekend);
    }
}
