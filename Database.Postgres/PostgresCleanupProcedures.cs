using Npgsql;

namespace Database.Postgres;

/// <summary>
/// Postgres-tier-tegenhanger van de vier AVG-opschoonprocedures (#861):
/// <c>planner.sp_CleanupEmailVerwerking</c>, <c>planner.sp_CleanupClassificatieCorrectie</c>,
/// <c>avg.sp_CleanupTeambegeleiding</c>, <c>avg.sp_CleanupImportLog</c>.
/// <para>
/// <b>Architectuurbeslissing — C#-methoden, geen Postgres-functies.</b> Zelfde patroon als
/// <see cref="PostgresMergeOrchestrator"/> (#818): de procedurele logica leeft in C#, niet in
/// PL/pgSQL. Elke methode berekent zijn tijdgrenzen éénmalig in C# (<c>DateTime.UtcNow</c>) en geeft
/// ze als parameter mee aan zowel de UPDATE als de DELETE — dezelfde reden als het origineel: een
/// rij mag niet tussen de twee statements door van venster wisselen.
/// </para>
/// <para>
/// <b>Twee-fase-anonimisering ongewijzigd:</b> elke procedure anonimiseert eerst PII in rijen binnen
/// het "grijze gebied" (ouder dan de anonimiseergrens, jonger dan de verwijdergrens), en verwijdert
/// daarna alles ouder dan de verwijdergrens. <c>IsBeantwoord</c>/<c>VerzendPogingOpUtc</c> worden in
/// <see cref="CleanupEmailVerwerkingAsync"/> bewust NOOIT geanonimiseerd (#718/#716) — die twee
/// kolommen zijn geen persoonsgegevens en dragen twee harde grenzen (replydetectie,
/// dubbele-verzending-bescherming).
/// </para>
/// <para>
/// <b>FK-opruimvolgorde (#424):</b> <c>planner.classificatiecorrectie</c> heeft twee FK's naar
/// <c>planner.emailverwerking</c> zonder <c>ON DELETE CASCADE</c> (Postgres kent dezelfde
/// meervoudige-cascadepad-beperking als SQL Server hier). <see cref="CleanupEmailVerwerkingAsync"/>
/// ruimt daarom eerst verwijzende correctierijen op (fase 2a) vóórdat het de ouderrij verwijdert
/// (fase 2b) — een correctierij kan jonger zijn dan de e-mailrij waarnaar hij verwijst
/// (replydetectie kent geen tijdgrens), dus <see cref="CleanupClassificatieCorrectieAsync"/>'s eigen
/// 90-dagengrens ruimt zo'n rij niet vanzelf op.
/// </para>
/// </summary>
public static class PostgresCleanupProcedures
{
    public static async Task CleanupEmailVerwerkingAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var anonimiseerVanaf = now.AddDays(-30);
        var verwijderVoor = now.AddDays(-90);

        await using (var update = new NpgsqlCommand(@"
            UPDATE planner.emailverwerking
            SET afzender = '[geanonimiseerd]',
                onderwerp = '[geanonimiseerd]',
                verstuurdnaar = NULL,
                emailbody = NULL,
                antwoordemail = NULL,
                plannerresponse = NULL,
                geextraheerdedata = NULL,
                foutmelding = NULL,
                mta_modified = @now
            WHERE mta_inserted < @anonimiseerVanaf
              AND mta_inserted >= @verwijderVoor
              AND (afzender <> '[geanonimiseerd]'
                   OR emailbody IS NOT NULL
                   OR antwoordemail IS NOT NULL
                   OR plannerresponse IS NOT NULL
                   OR geextraheerdedata IS NOT NULL
                   OR foutmelding IS NOT NULL)", connection))
        {
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("anonimiseerVanaf", anonimiseerVanaf);
            update.Parameters.AddWithValue("verwijderVoor", verwijderVoor);
            await update.ExecuteNonQueryAsync(ct);
        }

        await using (var deleteCorrecties = new NpgsqlCommand(@"
            DELETE FROM planner.classificatiecorrectie cc
            WHERE EXISTS (
                SELECT 1 FROM planner.emailverwerking ev
                WHERE ev.id IN (cc.origineleverwerkingid, cc.correctionverwerkingid)
                  AND ev.mta_inserted < @verwijderVoor
            )", connection))
        {
            deleteCorrecties.Parameters.AddWithValue("verwijderVoor", verwijderVoor);
            await deleteCorrecties.ExecuteNonQueryAsync(ct);
        }

        await using var deleteEmails = new NpgsqlCommand(
            "DELETE FROM planner.emailverwerking WHERE mta_inserted < @verwijderVoor", connection);
        deleteEmails.Parameters.AddWithValue("verwijderVoor", verwijderVoor);
        await deleteEmails.ExecuteNonQueryAsync(ct);
    }

    public static async Task CleanupClassificatieCorrectieAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var anonimiseerVanaf = now.AddDays(-30);
        var verwijderVoor = now.AddDays(-90);

        await using (var update = new NpgsqlCommand(@"
            UPDATE planner.classificatiecorrectie
            SET originelesamenvatting = NULL,
                correctiesamenvatting = NULL,
                mta_modified = @now
            WHERE mta_inserted < @anonimiseerVanaf
              AND mta_inserted >= @verwijderVoor
              AND (originelesamenvatting IS NOT NULL OR correctiesamenvatting IS NOT NULL)", connection))
        {
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("anonimiseerVanaf", anonimiseerVanaf);
            update.Parameters.AddWithValue("verwijderVoor", verwijderVoor);
            await update.ExecuteNonQueryAsync(ct);
        }

        await using var delete = new NpgsqlCommand(
            "DELETE FROM planner.classificatiecorrectie WHERE mta_inserted < @verwijderVoor", connection);
        delete.Parameters.AddWithValue("verwijderVoor", verwijderVoor);
        await delete.ExecuteNonQueryAsync(ct);
    }

    public static async Task CleanupTeambegeleidingAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        var verwijderVoor = DateTime.UtcNow.AddYears(-1);
        await using var delete = new NpgsqlCommand(
            "DELETE FROM avg.teambegeleiding WHERE mta_imported < @verwijderVoor", connection);
        delete.Parameters.AddWithValue("verwijderVoor", verwijderVoor);
        await delete.ExecuteNonQueryAsync(ct);
    }

    public static async Task CleanupImportLogAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var anonimiseerVoor = now.AddDays(-90);
        var verwijderVoor = now.AddYears(-1);

        await using (var update = new NpgsqlCommand(@"
            UPDATE avg.importlog
            SET importerendedoor = NULL,
                csvbestand = NULL
            WHERE importdatum < @anonimiseerVoor
              AND (importerendedoor IS NOT NULL OR csvbestand IS NOT NULL)", connection))
        {
            update.Parameters.AddWithValue("anonimiseerVoor", anonimiseerVoor);
            await update.ExecuteNonQueryAsync(ct);
        }

        await using var delete = new NpgsqlCommand(
            "DELETE FROM avg.importlog WHERE importdatum < @verwijderVoor", connection);
        delete.Parameters.AddWithValue("verwijderVoor", verwijderVoor);
        await delete.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Postgres-tegenhanger van <c>dbo.sp_CleanupAppSettingsAudit</c> (#781/#861).
    /// <para>
    /// <b>Waarom dit een AVG-gat dichtte en geen cosmetische aanvulling is:</b>
    /// <c>public.appsettingsaudit</c> bestond al sinds migratie 004 en legt bij elke
    /// instellingswijziging vast wié hem doorvoerde (<c>gewijzigddoor</c>) — een persoonsgegeven.
    /// De bewaartermijn-instelling (<c>appsettingsauditbewaardagen</c>) bestond eveneens al, maar
    /// er was op deze tier niets dat er ooit naar handelde: rijen bleven onbeperkt staan, in strijd
    /// met AVG art. 5 lid 1 sub e (opslagbeperking). Migratie 004 benoemde dit gat zelf al expliciet
    /// als "een van de resterende procedures uit #861".
    /// </para>
    /// <para>
    /// <b>Drietraps-terugval, letterlijk overgenomen uit het origineel</b> — de volgorde is
    /// betekenisdragend, niet toevallig:
    /// </para>
    /// <list type="number">
    /// <item>De primaire club (niet de <c>ALLSTARS</c>-democlub) is leidend voor
    /// deployment-brede instellingen, gesorteerd op <c>clubcode</c> — zelfde patroon als #598/#740.</item>
    /// <item>Vangnet als alleen de democlub bestaat (verse fork vóór de eerste echte configuratie):
    /// dan telt wél de democlubwaarde mee.</item>
    /// <item>Ontbrekende of onzinnige waarde (<c>NULL</c> of <c>&lt;= 0</c>): terugvallen op de
    /// gedocumenteerde default van 730 dagen. Bewust een default en géén "dan maar niets opruimen":
    /// dat laatste zou een configuratiefout stilzwijgend in een AVG-overtreding laten ontaarden.</item>
    /// </list>
    /// <para>
    /// <b>Tijdrekenen in C#, niet in SQL</b> (zelfde keuze als de vier procedures hierboven): de
    /// grens wordt éénmalig berekend en als parameter meegegeven, zodat hij niet per rij opnieuw
    /// geëvalueerd wordt. <c>tijdstip</c> is <c>TIMESTAMPTZ</c> en de grens is een UTC-
    /// <see cref="DateTime"/>, dus de vergelijking is absoluut — een databaseserver in een andere
    /// tijdzone (de zelftest draait bewust op Europe/Amsterdam, #854) verschuift het venster niet.
    /// </para>
    /// </summary>
    public static async Task CleanupAppSettingsAuditAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        const int standaardBewaarDagen = 730;

        // COALESCE over twee geordende subselects zet de drietraps-terugval in één query, zonder
        // round-trip per stap. NULLIF vangt stap 3 af: een waarde <= 0 wordt NULL en valt daarmee
        // door naar de default, exact zoals het origineel se `IF @BewaarDagen IS NULL OR <= 0`.
        int bewaarDagen;
        await using (var lees = new NpgsqlCommand(@"
            SELECT COALESCE(
                (SELECT NULLIF(GREATEST(appsettingsauditbewaardagen, 0), 0)
                 FROM public.appsettings
                 WHERE clubcode <> 'ALLSTARS'
                 ORDER BY clubcode
                 LIMIT 1),
                (SELECT NULLIF(GREATEST(appsettingsauditbewaardagen, 0), 0)
                 FROM public.appsettings
                 ORDER BY clubcode
                 LIMIT 1),
                @standaard)", connection))
        {
            lees.Parameters.AddWithValue("standaard", standaardBewaarDagen);
            var waarde = await lees.ExecuteScalarAsync(ct);
            bewaarDagen = waarde is null or DBNull ? standaardBewaarDagen : Convert.ToInt32(waarde);
        }

        var verwijderVoor = DateTime.UtcNow.AddDays(-bewaarDagen);

        await using var delete = new NpgsqlCommand(
            "DELETE FROM public.appsettingsaudit WHERE tijdstip < @verwijderVoor", connection);
        delete.Parameters.AddWithValue("verwijderVoor", verwijderVoor);
        await delete.ExecuteNonQueryAsync(ct);
    }
}
