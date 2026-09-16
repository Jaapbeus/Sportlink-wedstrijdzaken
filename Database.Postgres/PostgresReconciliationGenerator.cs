namespace Database.Postgres;

/// <summary>
/// Genereert het reconciliatie-/soft-delete-statement (#1193): markeert his-rijen die niet meer
/// voorkomen in de zojuist geladen stg-snapshot als verwijderd (<c>mta_deleted</c>), in plaats van
/// ze voor altijd te laten staan. "Sportlink is de waarheid altijd" — een rij die niet meer
/// terugkomt in een sync die het venster van die rij daadwerkelijk bevraagd heeft, is bij Sportlink
/// verdwenen.
/// <para>
/// <b>Nooit een hard delete.</b> <c>his</c> is bedoeld als audit-trail — een verdwenen wedstrijd of
/// team wordt daarom gemarkeerd (<c>mta_deleted = NOW()</c>), niet fysiek verwijderd. Zelfde
/// principe als <c>FunctionApp.Postgres.TeamResolution.TeamCanonicalisatieService</c>'
/// <c>DeactiveerOntbrekendeTeamsAsync</c>, dat <c>public.teams.isactief</c> op <c>false</c> zet in
/// plaats van de rij te verwijderen.
/// </para>
/// <para>
/// <b>Scope is de verantwoordelijkheid van de aanroeper</b> (zie
/// <see cref="PostgresMergeOrchestrator.ReconcileFullScopeAsync"/> en
/// <see cref="PostgresMergeOrchestrator.ReconcileWindowedAsync"/>): minimaal <c>clubcode</c> — nooit
/// clubs buiten de zojuist gesyncte club raken, met name nooit de AllStars FC-democlub, die geen
/// eigen Sportlink-sync heeft — en voor een entiteit met een datumvenster (vandaag alleen
/// <c>matches</c>) ook een <paramref name="dateColumn"/>-venster dat overeenkomt met wat déze
/// sync-run daadwerkelijk heeft opgehaald.
/// </para>
/// </summary>
public static class PostgresReconciliationGenerator
{
    /// <summary>
    /// Bouwt <c>UPDATE his."&lt;table&gt;" SET mta_deleted = NOW() WHERE ...</c>. Vergelijkt via
    /// dezelfde business-key-expressie als de upsert
    /// (<see cref="PostgresSchemaGenerator.BuildBusinessKeyExpression"/>), zodat een latere wijziging
    /// aan de business-key-definitie niet kan resulteren in twee uiteenlopende vergelijkingen.
    /// <paramref name="dateColumn"/> is optioneel: alleen meegeven voor een entiteit waarvan de sync
    /// een datumvenster kent (vandaag alleen <c>matches</c>, via <c>kaledatum</c>) — de parameters
    /// <c>@van</c>/<c>@tot</c> horen er dan verplicht bij (zie <c>ReconcileWindowedAsync</c>).
    /// Zonder <paramref name="dateColumn"/> is de volledige clubscope in <c>stg</c> het venster —
    /// alleen correct voor een entiteit die <b>niet</b> per weekoffset gesynchroniseerd wordt
    /// (vandaag alleen <c>teams</c>, via <c>ReconcileFullScopeAsync</c>).
    /// </summary>
    public static string GenerateSoftDeleteMissing(EntityDefinition entity, string? dateColumn = null)
    {
        var table = PostgresIdentifier.Quote(entity.EntityName);
        var bkColumn = PostgresIdentifier.Quote(PostgresSchemaGenerator.BusinessKeyColumnName(entity));
        var mtaDeleted = PostgresIdentifier.Quote("mta_deleted");
        var clubCode = PostgresIdentifier.Quote("clubcode");
        var bkExpression = PostgresSchemaGenerator.BuildBusinessKeyExpression(entity);

        // Defense in depth: de NOT EXISTS-subquery scoopt zelf óók op clubcode wanneer de entiteit
        // die kolom heeft. In de huidige deployment (#1193-context: één primaire club per
        // installatie, zie CLAUDE.md "Deployment-model") bevat stg altijd al maar één club per
        // sync-run, dus dit verandert vandaag geen enkel resultaat — maar zonder deze extra clausule
        // zou een toevallige business-key-botsing tussen twee clubs (bijv. een Sportlink-teamcode
        // die niet clubbreed uniek blijkt) een his-rij van de ene club onterecht als "nog aanwezig"
        // kunnen laten tellen op basis van stg-data van een andere club.
        var stgClubScope = entity.HasClubCode ? $" AND stg.{table}.{clubCode} = @clubCode" : "";

        var sql =
            $"UPDATE his.{table}\n" +
            $"SET {mtaDeleted} = NOW()\n" +
            $"WHERE {mtaDeleted} IS NULL\n" +
            $"  AND {clubCode} = @clubCode\n" +
            $"  AND NOT EXISTS (\n" +
            $"      SELECT 1 FROM stg.{table} WHERE ({bkExpression}) = his.{table}.{bkColumn}{stgClubScope}\n" +
            $"  )\n";

        if (dateColumn != null)
        {
            var dateCol = PostgresIdentifier.Quote(dateColumn);
            sql += $"  AND {dateCol}::date BETWEEN @van AND @tot\n";
        }

        return sql + ";\n";
    }
}
