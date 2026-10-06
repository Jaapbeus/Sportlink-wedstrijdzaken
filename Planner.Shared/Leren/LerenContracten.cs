namespace Planner.Shared.Leren;

/// <summary>
/// Wie een leeractie (alias aanmaken, leermoment toevoegen, wachtrijregel afhandelen) uitvoert (#1568 deel C).
/// Uitsluitend server-side uit het Easy Auth-principal bepaald, nooit uit de requestbody — zelfde
/// regel als <c>FeedbackAanroeper</c> (#764). Er wordt geen e-mailadres bewaard: alleen de Entra
/// object-ID (pseudoniem) en een momentopname van de weergavenaam.
/// </summary>
public sealed record LerenAanroeper(string? ObjectId, string? Naam)
{
    public const int MaxIdLengte = 64;
    public const int MaxNaamLengte = 100;

    /// <summary>
    /// Lokaal (helemaal geen principal: noch oid noch naam) is de aanroeper de lokale ontwikkelaar; een principal
    /// zonder oid — ook als de naam ontbreekt — is "onbekend", zodat een echte aanroeper nooit als lokaal wordt vastgelegd.
    /// </summary>
    public string DoorId => Kap(!string.IsNullOrWhiteSpace(ObjectId) ? ObjectId
        : HeeftPrincipal || !string.IsNullOrWhiteSpace(Naam) ? "onbekend" : "lokale-ontwikkelaar", MaxIdLengte)!;

    /// <summary>Of er een Easy Auth-principal hoort te zijn (productie: <c>WEBSITE_SITE_NAME</c> gezet); alleen lokaal is dat <c>false</c>.</summary>
    public bool HeeftPrincipal { get; init; }

    /// <summary>Momentopname van de weergavenaam; <c>null</c> als die ontbreekt.</summary>
    public string? DoorNaam => Kap(Naam, MaxNaamLengte);

    private static string? Kap(string? waarde, int max)
        => string.IsNullOrWhiteSpace(waarde) ? null : waarde.Length <= max ? waarde : waarde[..max];
}

public enum AliasAanmaakStatus { Aangemaakt, Herkoppeld, BestaatAl, Conflict, TeamOnbekend }

/// <param name="Genormaliseerd">Uitkomst van <c>TeamNaamNormalisatie.NormaliseerVoorVergelijking</c>; nooit zelf berekend door de tier.</param>
/// <param name="Herkoppel">Alleen als de beheerder dat expliciet vroeg: een bestaande alias met dezelfde sleutel wijst dan naar dit team.</param>
public sealed record AliasAanmaakOpdracht(
    string ClubCode, string RuweTekst, string Genormaliseerd, int TeamId, bool Herkoppel,
    LerenAanroeper Wie, int? HerkomstVerwerkingId, string? Reden);

public sealed record AliasAanmaakUitkomst(
    AliasAanmaakStatus Status, int? Id = null, string? Teamnaam = null,
    int? BestaandTeamId = null, string? BestaandTeamnaam = null, string? BestaandeStatus = null);

/// <summary>Tier-eigen opslag voor het aanmaken van een gevalideerde alias (bron <c>CoordinatorCorrectie</c>).</summary>
public interface ITeamAliasStore
{
    Task<AliasAanmaakUitkomst> MaakAanAsync(AliasAanmaakOpdracht opdracht);
}

public static class OnbekendeTeamTekstStatus
{
    public const string Open = "open";
    public const string Afgehandeld = "afgehandeld";
    public const string Genegeerd = "genegeerd";

    public static readonly IReadOnlyList<string> Alle = [Open, Afgehandeld, Genegeerd];
}

public sealed record OnbekendeTeamTekstRij(
    int Id, string Voorbeeld, string Genormaliseerd, int Aantal,
    DateTime EerstGezien, DateTime LaatstGezien, int? LaatsteVerwerkingId, string Status);

/// <summary>Wat de pipeline wegschrijft als een teamtekst niet herkend werd (gedeeld door beide tiers).</summary>
public interface IOnbekendeTeamTekstSchrijver
{
    /// <summary>Upsert op (clubcode, genormaliseerd); een retry van dezelfde verwerking telt niet dubbel.</summary>
    Task VoegToeAsync(string clubCode, string genormaliseerd, string voorbeeld, int verwerkingId);
}

/// <summary>Beheerkant van de wachtrij met onbekende teamteksten.</summary>
public interface IOnbekendeTeamTekstStore : IOnbekendeTeamTekstSchrijver
{
    Task<IReadOnlyList<OnbekendeTeamTekstRij>> LijstAsync(string clubCode, string? status, int limit);
    Task<int> AantalOpenAsync(string clubCode);
    Task<int> ZetStatusAsync(string clubCode, int id, string status);

    /// <summary>Zet open regels met deze sleutel op <c>afgehandeld</c> (aanroep na het aanmaken van een alias).</summary>
    Task<int> MarkeerAfgehandeldAsync(string clubCode, string genormaliseerd);
}

/// <param name="OrigineelType">Mag ontbreken; de opslag vervangt het door <see cref="Planner.Shared.Email.LeermomentInvoer.OnbekendType"/>.</param>
public sealed record AdminLeermomentOpdracht(
    string ClubCode, string OrigineelType, string JuistType, string Samenvatting,
    LerenAanroeper Wie, int? HerkomstVerwerkingId);
