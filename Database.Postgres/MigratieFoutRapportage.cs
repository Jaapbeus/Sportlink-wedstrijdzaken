using Npgsql;

namespace Database.Postgres;

/// <summary>
/// #1225: één plek die een mislukte database-handeling naar een regel voor de console vertaalt —
/// zonder de tekst van de onderliggende exception.
/// <para>
/// <b>Waarom de foutmelding zelf niet meegaat.</b> <c>Database.Postgres.Cli</c> draait in de job
/// <c>db-migrate-postgres</c> van <c>deploy.yml</c> (§57), en de Actions-logs van deze repository
/// zijn publiek. GitHub maskeert uitsluitend de exacte, volledige waarde van een secret — niet een
/// deelstring daarvan. Een Npgsql-verbindingsfout luidt "Failed to connect to &lt;host&gt;:&lt;poort&gt;"
/// en een authenticatiefout kan de gebruikersnaam noemen; bij de gehoste provider draagt die naam
/// de projectidentificatie. Beide zijn deelstrings van <c>POSTGRES_CONNECTION_STRING</c> en komen
/// dus ongemaskeerd in een publiek log terecht. Dezelfde foutklasse als #1200, waar de clientId via
/// een log-URL lekte.
/// </para>
/// <para>
/// <b>Waarom dit toch bruikbaar blijft om mee te zoeken.</b> Wat wél wordt gemeld is het
/// exceptietype, de SQLSTATE-code bij een <see cref="PostgresException"/> (vijf tekens, vast
/// gedefinieerd door Postgres — nooit gebruikersdata) en de migratiestap waar het misging. Dat is
/// genoeg om de oorzaak te bepalen: het bestand wijst de SQL aan, de SQLSTATE de foutklasse. De
/// volledige melding staat in de databaselogs van de provider en is lokaal reproduceerbaar.
/// </para>
/// <para>
/// <b>Bewust geen conditionele variant</b> ("wél de volledige melding als de omgeving niet-publiek
/// is"). Een schakelaar die bepaalt of een secret in een log belandt, is precies één
/// configuratiefout verwijderd van een lek. De altijd-veilige vorm kost hier niets dat niet
/// elders — in de databaselogs of lokaal — terug te vinden is.
/// </para>
/// </summary>
public static class MigratieFoutRapportage
{
    private const string Toelichting =
        "De volledige foutmelding is bewust weggelaten: CI-uitvoer van een publieke repository is " +
        "publiek en een databasefout draagt vaak host of gebruikersnaam mee (#1225). " +
        "Raadpleeg de databaselogs van de provider, of reproduceer de handeling lokaal.";

    /// <summary>
    /// Bouwt één regel voor stderr. <paramref name="handeling"/> is de aanhef in de derde persoon
    /// zonder werkwoord ("Migratie", "Aanmaken van de his-tabellen"); <paramref name="stap"/> is de
    /// migratiestap, tabel of het bestand waarbij het misging, of <c>null</c> als de fout vóór de
    /// eerste stap optrad (bijvoorbeeld bij het openen van de verbinding).
    /// </summary>
    public static string Beschrijf(string handeling, Exception fout, string? stap = null)
    {
        var soort = BeschrijfSoort(fout);
        var waar = string.IsNullOrWhiteSpace(stap) ? string.Empty : $" bij '{stap}'";
        return $"{handeling} mislukt{waar} ({soort}). {Toelichting}";
    }

    /// <summary>
    /// Het exceptietype, aangevuld met de SQLSTATE-code als Postgres die meegaf. Alleen typenaam en
    /// SQLSTATE — nooit <c>Message</c>, <c>Detail</c>, <c>Hint</c> of <c>ToString()</c>, want die
    /// kunnen host, gebruikersnaam of rijinhoud bevatten.
    /// </summary>
    private static string BeschrijfSoort(Exception fout) => fout switch
    {
        PostgresException pg => $"{nameof(PostgresException)}, SQLSTATE {pg.SqlState}",
        _ => fout.GetType().Name
    };
}
