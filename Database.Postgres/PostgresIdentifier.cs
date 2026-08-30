namespace Database.Postgres;

/// <summary>
/// Quote't identifiers voor gegenereerde Postgres-DDL/DML (#818).
/// <para>
/// Postgres vouwt een ongequote identifier automatisch naar lowercase (<c>CREATE TABLE Teams</c>
/// wordt intern <c>teams</c>); een latere, gequote referentie (<c>"Teams"</c>) matcht daar niet
/// meer mee en faalt. Elke identifier die deze laag genereert wordt daarom altijd gequote, zodat
/// de exacte, aangeleverde casing behouden blijft — zie
/// docs/ARCHITECTUUR-DATABASE-TIERS.md sectie 3 (empirisch bevestigd, Postgres 16, 2026-08-30).
/// </para>
/// </summary>
public static class PostgresIdentifier
{
    public static string Quote(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("Identifier mag niet leeg zijn.", nameof(identifier));

        // Verdubbel een eventueel dubbel aanhalingsteken in de naam zelf (SQL-standaard escape) —
        // voorkomt dat een kwaadaardige of foutieve kolomnaam de gegenereerde SQL kan breken.
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }
}
