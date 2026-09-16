using Npgsql;

namespace Database.Postgres;

/// <summary>
/// Postgres-equivalent van <c>FunctionApp/MergeStgToHis.cs</c> (#818): voert de door
/// <see cref="PostgresSchemaGenerator"/>/<see cref="PostgresUpsertGenerator"/> gegenereerde SQL
/// uit tegen een Postgres-database via Npgsql. Alle dynamische logica (kolomtypen, business key,
/// change-detection) zit al vast in de gegenereerde stringen — deze klasse introspecteert zelf
/// niets, exact zoals de bestaande SQL-Server-orchestrator dat ook niet doet.
/// </summary>
public sealed class PostgresMergeOrchestrator
{
    private readonly string _connectionString;

    public PostgresMergeOrchestrator(string connectionString) => _connectionString = connectionString;

    /// <summary>Verwijdert en herbouwt de stg-tabel — analoog aan CreateTable.cs' bestaande
    /// DROP-TABLE-IF-EXISTS-patroon voor SQL Server.</summary>
    public async Task RecreateStgTableAsync(EntityDefinition entity, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await EnsureSchemaAsync(connection, "stg", ct);
        await using var command = new NpgsqlCommand(PostgresSchemaGenerator.GenerateStgTable(entity), connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Zorgt dat de his-tabel bestaat (idempotent) — analoog aan
    /// sp_CreateTargetTableFromSource, maar hier vooraf C#-gegenereerd i.p.v.
    /// runtime-catalogus-introspectie.</summary>
    public async Task EnsureHisTableAsync(EntityDefinition entity, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await EnsureSchemaAsync(connection, "his", ct);
        await using var command = new NpgsqlCommand(PostgresSchemaGenerator.GenerateHisTable(entity), connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Schept het <c>stg</c>/<c>his</c>-schema zelf als het nog niet bestaat. Vóór deze aanvulling
    /// bestond er geen enkele plek — productiecode of test — die deze schema's daadwerkelijk
    /// aanmaakte; elke test moest dat zelf via een losse <c>CREATE SCHEMA</c>-aanroep doen vóórdat
    /// de orchestrator iets kon uitvoeren. Idempotent, dus geen effect op een reeds bestaand schema.
    /// </summary>
    private static async Task EnsureSchemaAsync(NpgsqlConnection connection, string schema, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"CREATE SCHEMA IF NOT EXISTS {PostgresIdentifier.Quote(schema)};", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Voert de upsert van stg naar his uit voor deze entiteit — analoog aan
    /// sp_MergeStgToHis' MERGE-statement.</summary>
    public async Task MergeStgToHisAsync(EntityDefinition entity, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(PostgresUpsertGenerator.GenerateUpsertFromStgToHis(entity), connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Reconcilieert een entiteit ZONDER datumvenster (#1193; vandaag alleen <c>teams</c>): elke
    /// his-rij van deze club die niet meer in de zojuist geladen stg-snapshot voorkomt wordt
    /// gemarkeerd als verwijderd (<c>mta_deleted = NOW()</c>), nooit hard verwijderd.
    /// <para>
    /// Veilig zonder venster omdat <c>/teams</c> geen weekoffset-parameter kent: <c>stg.teams</c> is
    /// bij elke sync-run een complete snapshot van de hele club, dus afwezigheid daarin is
    /// ondubbelzinnig "niet meer bij Sportlink" — in tegenstelling tot <c>matches</c>, waar een
    /// sync-run maar een deelvenster van de kalender bevraagt (zie
    /// <see cref="ReconcileWindowedAsync"/>).
    /// </para>
    /// <para>
    /// Roep dit alleen aan wanneer de bijbehorende fetch-fase (<c>/teams</c>) zonder fouten
    /// verliep — bij een mislukte/onvolledige fetch is <c>stg.teams</c> geen betrouwbare volledige
    /// snapshot en zou reconciliatie legitieme teams onterecht als verdwenen markeren. Die keuze
    /// hoort bij de aanroeper (<c>PostgresSyncPipeline</c>), niet bij deze methode.
    /// </para>
    /// </summary>
    public async Task<int> ReconcileFullScopeAsync(EntityDefinition entity, string clubCode, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            PostgresReconciliationGenerator.GenerateSoftDeleteMissing(entity), connection);
        command.Parameters.AddWithValue("clubCode", clubCode);
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Reconcilieert een entiteit MET datumvenster (#1193; vandaag alleen <c>matches</c>, via
    /// <paramref name="dateColumn"/> <c>"kaledatum"</c>).
    /// <para>
    /// <b>Het venster wordt niet uit de weekoffset-parameters herberekend.</b> <c>PostgresSyncPipeline</c>
    /// vraagt <c>/programma</c>/<c>/uitslagen</c> op met een weekoffset die Sportlink zelf naar een
    /// kalenderweek vertaalt — hoe Sportlink die vertaling precies uitvoert (welke dag een "week"
    /// begint) is niet gedocumenteerd en dus niet hard na te bouwen aan de app-kant. In plaats
    /// daarvan wordt het venster afgeleid uit de MIN/MAX van <paramref name="dateColumn"/> die déze
    /// sync-run daadwerkelijk in <c>stg</c> heeft geladen voor deze club: dat garandeert dat er
    /// nooit buiten het werkelijk bevraagde venster gereconcilieerd wordt. In het ergste geval is
    /// het afgeleide venster iets smaller dan wat Sportlink echt query't (bijv. wanneer de rand van
    /// het venster toevallig geen enkele wedstrijd meer oplevert) — dat is zelfhelend bij de
    /// eerstvolgende sync zodra het venster opschuift, en is bewust veiliger dan het omgekeerde
    /// risico (per ongeluk legitieme data buiten het venster verwijderen).
    /// </para>
    /// <para>
    /// <b>Levert <c>stg</c> géén enkele rij op voor deze club, dan wordt er NIETS gereconcilieerd</b>
    /// — er is dan simpelweg geen datum om een venster op te baseren. Dit dekt zowel "de fetch
    /// mislukte" (de aanroeper moet deze methode sowieso alleen aanroepen als de fetch-fase zonder
    /// fouten verliep — zie <see cref="ReconcileFullScopeAsync"/> voor dezelfde afweging bij
    /// <c>teams</c>) als "de fetch slaagde, maar leverde voor dit venster écht nul wedstrijden op"
    /// (bijv. een smal "volgende week"-sync-venster zonder enige geplande wedstrijd). Het tweede
    /// geval is een bewuste, geaccepteerde beperking: een his-rij die toevallig precies in zo'n
    /// volledig lege sync-run zou moeten reconciliëren blijft die ene run staan, maar wordt alsnog
    /// gevonden zodra een latere sync-run wél minstens één wedstrijd in een venster oplevert dat
    /// deze rij's datum omvat — zelfhelend, net als de "iets te smal venster"-situatie hierboven, en
    /// nog steeds veiliger dan een wal-klok-gebaseerde schatting van Sportlink's weekindeling
    /// gebruiken (met het risico op de andere kant: per ongeluk data buiten het echte venster raken).
    /// </para>
    /// </summary>
    public async Task<int> ReconcileWindowedAsync(
        EntityDefinition entity, string clubCode, string dateColumn, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var table = PostgresIdentifier.Quote(entity.EntityName);
        var dateCol = PostgresIdentifier.Quote(dateColumn);
        var clubCol = PostgresIdentifier.Quote("clubcode");

        DateOnly van, tot;
        await using (var boundsCommand = new NpgsqlCommand(
            $"SELECT MIN({dateCol}::date), MAX({dateCol}::date) FROM stg.{table} WHERE {clubCol} = @clubCode;",
            connection))
        {
            boundsCommand.Parameters.AddWithValue("clubCode", clubCode);
            await using var reader = await boundsCommand.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.IsDBNull(0) || reader.IsDBNull(1))
                return 0; // Geen stg-rijen voor deze club dit run — niets om tegen te reconciliëren.
            van = reader.GetFieldValue<DateOnly>(0);
            tot = reader.GetFieldValue<DateOnly>(1);
        }

        await using var command = new NpgsqlCommand(
            PostgresReconciliationGenerator.GenerateSoftDeleteMissing(entity, dateColumn), connection);
        command.Parameters.AddWithValue("clubCode", clubCode);
        command.Parameters.AddWithValue("van", van);
        command.Parameters.AddWithValue("tot", tot);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
