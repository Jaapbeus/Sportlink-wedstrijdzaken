using System.Data.Common;

namespace Planner.Shared.TeamResolution;

/// <summary>
/// Smalle poort naar de teambegeleiding (#1862): welke teams begeleidt de houder van dit e-mailadres? Per
/// databasetier één implementatie. De poort geeft uitsluitend ruwe teamwaarden terug (zoals in de begeleiding
/// staan, bijvoorbeeld <c>O23-1</c>), nooit naam, telefoonnummer of rol, en de teamwaarden gaan daarna door de
/// <see cref="ITeamResolver"/>: er is geen tweede teamnaamlogica.
/// </summary>
public interface IAfzenderTeamBron
{
    /// <summary>
    /// De verschillende teamwaarden waarbij <paramref name="afzender"/> in de begeleiding van <paramref name="clubCode"/>
    /// staat. <paramref name="afzender"/> is al genormaliseerd (<see cref="AfzenderTeamBronHulp.NormaliseerAdres"/>: getrimd,
    /// kleine letters, nooit leeg); de bron vergelijkt dat met de genormaliseerde waarde in de database. Geen treffer
    /// geeft een lege lijst.
    /// </summary>
    Task<IReadOnlyList<string>> ZoekTeamsVanAfzenderAsync(string afzender, string clubCode);
}

/// <summary>
/// De gegevens waarmee <see cref="AfzenderTeamBepaling"/> het eigen team uit de afzender kan afleiden. Het
/// e-mailadres blijft hierin en gaat nooit naar trace, log of telemetrie.
/// </summary>
/// <param name="Bron">De tier-opzoeking in de teambegeleiding.</param>
/// <param name="Afzender">Het From-adres van het bericht.</param>
/// <param name="ReplyTo">De Reply-To-adressen van het bericht; leeg als er geen zijn.</param>
public sealed record AfzenderOpzoeking(IAfzenderTeamBron Bron, string? Afzender, IReadOnlyList<string>? ReplyTo);

/// <summary>
/// De gedeelde kant van een <see cref="IAfzenderTeamBron"/>: hoe het adres wordt genormaliseerd en hoe de rijen worden
/// gelezen. De tier houdt alleen de query en de parameterbinding.
/// </summary>
public static class AfzenderTeamBronHulp
{
    /// <summary>Het adres zoals de index het vergelijkt: getrimd en in kleine letters; leeg als er geen adres is.</summary>
    public static string NormaliseerAdres(string? afzender) => (afzender ?? "").Trim().ToLowerInvariant();

    /// <summary>Leest de teamkolom (kolom 0) van alle rijen; een lege waarde telt niet mee.</summary>
    public static async Task<IReadOnlyList<string>> LeesTeamsAsync(DbDataReader reader)
    {
        var teams = new List<string>();
        while (await reader.ReadAsync())
            if (!reader.IsDBNull(0) && !string.IsNullOrWhiteSpace(reader.GetString(0))) teams.Add(reader.GetString(0));
        return teams;
    }
}
