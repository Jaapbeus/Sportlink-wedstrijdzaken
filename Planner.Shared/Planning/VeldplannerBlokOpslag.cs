using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Planner.Shared.Integrations.SportlinkClub;

namespace Planner.Shared.Planning;

/// <summary>
/// Eén opgeslagen veldplanner-blok (#1915): wat Sportlink nu van een wedstrijd op het park weet, met
/// <see cref="PublicMatchId"/> als sleutel per club. Beide tiers slaan dit op in <c>sportlinkveldplannerblok</c>.
/// </summary>
/// <param name="ExternalMatchId">Het bondswedstrijdnummer; <c>null</c> zolang het nog niet bij Sportlink is opgevraagd.</param>
/// <param name="RijHash">Hash van de planningsvelden (<see cref="VeldplannerBlokHash"/>): gelijk = niets te schrijven.</param>
public sealed record VeldplannerBlokRij(
    string ClubCode, string PublicMatchId, DateOnly Datum, string Label, string Veld, string StartTijd,
    decimal Veldafmeting, int DuurMinuten, long? ExternalMatchId, string RijHash);

/// <summary>De bestaande opslag van één dag, kort genoeg om per planningsaanvraag te vergelijken.</summary>
public sealed record BlokOpslagStand(string RijHash, long? ExternalMatchId);

/// <summary>
/// De opslag van de veldplanner-blokken. Elke databasetier implementeert dit met zijn eigen driver; de
/// vergelijkingslogica staat gedeeld in <see cref="VeldplannerBlokSynchronisatie"/> (regel 1 van de codekwaliteit).
/// </summary>
public interface IVeldplannerBlokStore
{
    /// <summary>De opgeslagen blokken van deze club op deze datum, per <c>PublicMatchId</c>.</summary>
    Task<IReadOnlyDictionary<string, BlokOpslagStand>> LeesDagAsync(string clubCode, DateOnly datum, CancellationToken ct = default);

    /// <summary>
    /// Schrijft de rijen (invoegen of bijwerken op <c>ClubCode</c> + <c>PublicMatchId</c>) en verwijdert de genoemde
    /// sleutels van deze datum, in één transactie. Verwijdert daarnaast alles van deze club voor
    /// <paramref name="bewaarVanaf"/>, zodat de tabel niet aangroeit.
    /// </summary>
    Task SchrijfAsync(string clubCode, DateOnly datum, IReadOnlyList<VeldplannerBlokRij> rijen,
        IReadOnlyList<string> verwijderSleutels, DateOnly bewaarVanaf, CancellationToken ct = default);
}

/// <summary>De hash waarmee een planningsaanvraag per sleutel ziet of een blok in Sportlink veranderd is.</summary>
public static class VeldplannerBlokHash
{
    /// <summary>SHA-256 (hex, kleine letters) over label, veld, starttijd, afmeting en duur — niet over de sleutel zelf.</summary>
    public static string Bereken(SportlinkVeldplannerBlok blok)
    {
        var tekst = string.Join('\u001f', blok.Label, blok.Veld, blok.StartTijd,
            blok.Veldafmeting.ToString("0.####", CultureInfo.InvariantCulture),
            blok.DuurMinuten.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tekst)));
    }
}

/// <summary>Wat er bij een planningsaanvraag naar de tabel moet: alleen verschillen, bepaald op sleutel en hash.</summary>
/// <param name="Toevoegen">Sleutels die nog niet in de tabel staan.</param>
/// <param name="Wijzigen">Sleutels met een andere hash.</param>
/// <param name="Verwijderen">Sleutels die er staan maar niet meer in Sportlink voorkomen.</param>
/// <param name="Ongewijzigd">Aantal sleutels met dezelfde hash.</param>
/// <param name="ZonderSleutel">Blokken zonder <c>PublicMatchId</c>: kunnen niet worden opgeslagen of gekoppeld.</param>
public sealed record VeldplannerBlokDiff(
    IReadOnlyList<SportlinkVeldplannerBlok> Toevoegen, IReadOnlyList<SportlinkVeldplannerBlok> Wijzigen,
    IReadOnlyList<string> Verwijderen, int Ongewijzigd, int ZonderSleutel)
{
    /// <summary>
    /// Vergelijkt de huidige Sportlink-blokken met de opslag van die dag, uitsluitend op sleutel en hash. Dezelfde sleutel
    /// twee keer in één antwoord: de laatste vermelding wint (zoals bij de staging, #1712).
    /// </summary>
    public static VeldplannerBlokDiff Bepaal(
        IReadOnlyDictionary<string, BlokOpslagStand> bestaand, IReadOnlyList<SportlinkVeldplannerBlok> blokken)
    {
        var nieuw = new Dictionary<string, SportlinkVeldplannerBlok>(StringComparer.Ordinal);
        var zonderSleutel = 0;
        foreach (var blok in blokken)
        {
            if (string.IsNullOrWhiteSpace(blok.PublicMatchId)) { zonderSleutel++; continue; }
            nieuw[blok.PublicMatchId] = blok;
        }

        var toevoegen = new List<SportlinkVeldplannerBlok>();
        var wijzigen = new List<SportlinkVeldplannerBlok>();
        var ongewijzigd = 0;
        foreach (var (sleutel, blok) in nieuw)
        {
            if (!bestaand.TryGetValue(sleutel, out var stand)) toevoegen.Add(blok);
            else if (!string.Equals(stand.RijHash, VeldplannerBlokHash.Bereken(blok), StringComparison.Ordinal)) wijzigen.Add(blok);
            else ongewijzigd++;
        }

        var verwijderen = bestaand.Keys.Where(k => !nieuw.ContainsKey(k)).ToList();
        return new VeldplannerBlokDiff(toevoegen, wijzigen, verwijderen, ongewijzigd, zonderSleutel);
    }
}
