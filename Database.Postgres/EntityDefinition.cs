namespace Database.Postgres;

/// <summary>
/// Provider-agnostisch kolomtype. Vertaling naar Postgres-DDL-syntax gebeurt in
/// <see cref="PostgresTypeMapper"/> (#818) — zie docs/ARCHITECTUUR-DATABASE-TIERS.md voor de
/// bredere architectuurcontext (epic #815).
/// </summary>
public enum ProviderAgnosticType
{
    Integer,
    BigInt,
    Text,
    VarChar,
    Boolean,
    Timestamp,
    Date,
    Time,
    Decimal
}

/// <summary>
/// Eén data-/businesskolom van een entiteit. Bevat uitsluitend kolommen die de bron-API/stg-laag
/// daadwerkelijk levert — nooit audit-kolommen, nooit de surrogate-sleutel, nooit de
/// synthetische business-key-kolom. Die drie voegt <see cref="PostgresSchemaGenerator"/> zelf
/// toe aan de his-tabel, zodat een aanroeper ze niet per entiteit hoeft te herhalen (en dus ook
/// niet per ongeluk kan vergeten of inconsistent kan benoemen).
/// </summary>
public sealed record ColumnDefinition(
    string Name,
    ProviderAgnosticType Type,
    bool IsNullable = true,
    int? Length = null,
    int? Precision = null,
    int? Scale = null);

/// <summary>
/// Eén C#-schemadefinitie per entiteit — de single source of truth die zowel de Postgres-stg-
/// als de Postgres-his-tabelgeneratie voedt (#818), zonder afhankelijkheid van Postgres' eigen
/// systeemcatalogus tijdens runtime (in tegenstelling tot het SQL-Server-patroon, waar
/// sp_CreateTargetTableFromSource sys.* introspecteert tijdens executie).
/// </summary>
/// <param name="EntityName">Tabelnaam zonder schema, bijv. "teams" → stg.teams / his.teams.
/// Wordt ook gebruikt in de naam van de synthetische business-key-kolom (bk_&lt;EntityName&gt;).</param>
/// <param name="Columns">Uitsluitend data-/businesskolommen (geen audit, geen surrogate-sleutel).</param>
/// <param name="BusinessKey">
/// Geordende lijst kolomnamen (moeten voorkomen in <paramref name="Columns"/>) die samen de
/// business key vormen. Kolommen mogen NULL-baar zijn: Postgres' <c>UNIQUE</c>/<c>ON CONFLICT</c>
/// behandelt elke NULL in een composietsleutel als distinct, wat een tweede rij zou toevoegen
/// i.p.v. de bestaande bij te werken (#818-addendum, fact-check van de externe review). Daarom
/// legt de generator de unieke sleutel niet direct op deze kolommen, maar op een gegenereerde,
/// nooit-NULL synthetische kolom — een <c>COALESCE</c>-gebaseerde vertaling van SQL Server's
/// bestaande <c>bk_&lt;entiteit&gt;</c>-patroon (zie <see cref="PostgresSchemaGenerator"/>).
/// </param>
/// <param name="HasClubCode">Voegt een secundaire (niet-unieke) index toe op de clubcode-kolom.
/// Vereist dat "clubcode" voorkomt in <paramref name="Columns"/>.</param>
public sealed record EntityDefinition(
    string EntityName,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<string> BusinessKey,
    bool HasClubCode)
{
    /// <summary>
    /// Bouwt een <see cref="EntityDefinition"/> en valideert meteen de interne consistentie
    /// (business key verwijst naar bestaande kolommen, clubcode-kolom aanwezig indien
    /// <paramref name="hasClubCode"/>, alle identifiers volgen de lowercase-snake_case-conventie
    /// uit docs/ARCHITECTUUR-DATABASE-TIERS.md §3). De generatorklassen zelf gaan ervan uit dat een
    /// <see cref="EntityDefinition"/> al consistent is — deze factory is de plek waar dat
    /// afgedwongen wordt, in plaats van bij elke generator opnieuw.
    /// <para>
    /// <b>#855:</b> de casing-check bestaat omdat <see cref="PostgresIdentifier.Quote"/> elke
    /// identifier onvoorwaardelijk quote't — een PascalCase naam landt dan letterlijk zo in de
    /// database en breekt elke latere, ongequote verwijzing. <see cref="KnownEntities"/> week hier
    /// eerder (per ongeluk) van af; deze validatie voorkomt een stille regressie.
    /// </para>
    /// </summary>
    public static EntityDefinition Create(
        string entityName,
        IReadOnlyList<ColumnDefinition> columns,
        IReadOnlyList<string> businessKey,
        bool hasClubCode)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            throw new ArgumentException("entityName mag niet leeg zijn.", nameof(entityName));
        if (columns.Count == 0)
            throw new ArgumentException("Een entiteit moet minstens één kolom hebben.", nameof(columns));
        if (businessKey.Count == 0)
            throw new ArgumentException("Een entiteit moet een business key hebben.", nameof(businessKey));

        var slechteCasing = new[] { entityName }
            .Concat(columns.Select(c => c.Name))
            .Concat(businessKey)
            .Where(naam => naam != naam.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (slechteCasing.Count > 0)
            throw new ArgumentException(
                "Identifier(s) volgen de lowercase-snake_case-conventie niet " +
                $"(docs/ARCHITECTUUR-DATABASE-TIERS.md §3): {string.Join(", ", slechteCasing)}.",
                nameof(columns));

        var columnNames = columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var missing = businessKey.Where(k => !columnNames.Contains(k)).ToList();
        if (missing.Count > 0)
            throw new ArgumentException(
                $"Business key verwijst naar kolom(men) die niet in Columns voorkomen: {string.Join(", ", missing)}.",
                nameof(businessKey));

        if (hasClubCode && !columnNames.Contains("clubcode"))
            throw new ArgumentException(
                "hasClubCode is true maar 'clubcode' komt niet voor in columns.", nameof(hasClubCode));

        return new EntityDefinition(entityName, columns, businessKey, hasClubCode);
    }
}
