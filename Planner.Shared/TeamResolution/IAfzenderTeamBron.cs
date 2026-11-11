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
/// Een bron die bewust niet zoekt (#1862): de pipeline slaat de afzender-opzoeking dan over en de trace noemt de reden.
/// De beheerderstester (proefverwerking) gebruikt hem, omdat daar een mens een vrij ingetypt adres invoert en het
/// antwoord ziet: zo'n opzoeking zou tonen welk team iemand begeleidt, buiten het inzagelog (#1701) om. De automatische
/// verwerking van echte mail gebruikt de tier-bron.
/// </summary>
public sealed class OvergeslagenAfzenderTeamBron : IAfzenderTeamBron
{
    /// <summary>Voor de proefverwerking in de beheerderstester (<c>test/email</c>).</summary>
    public static readonly OvergeslagenAfzenderTeamBron Proefverwerking = new("proefverwerking door een beheerder");

    private OvergeslagenAfzenderTeamBron(string reden) => Reden = reden;

    /// <summary>De reden in de trace; nooit een adres of naam.</summary>
    public string Reden { get; }

    /// <summary>Wordt nooit aangeroepen: <see cref="AfzenderTeamBepaling"/> herkent deze bron vooraf.</summary>
    public Task<IReadOnlyList<string>> ZoekTeamsVanAfzenderAsync(string afzender, string clubCode)
        => throw new InvalidOperationException("De afzender-opzoeking is voor deze verwerking uitgeschakeld.");
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
