using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Monitoring;

/// <summary>
/// <see cref="INoodmailThrottleStore"/>-implementatie op basis van Azure Table Storage.
///
/// Gebruikt de bestaande <c>AzureWebJobsStorage</c>-opslagaccount (Azurite lokaal, de reguliere
/// Functions-opslagaccount in productie) — géén nieuwe Azure-resource. Eén tabel
/// (<see cref="TableName"/>), één rij per throttle-sleutel.
/// </summary>
public sealed class TableStorageNoodmailThrottleStore : INoodmailThrottleStore
{
    private const string TableName = "NoodmailThrottle";
    private const string PartitionKey = "noodmail";
    private const string VerstuurdOpUtcProperty = "VerstuurdOpUtc";

    private readonly TableClient _tableClient;
    private readonly ILogger<TableStorageNoodmailThrottleStore> _log;
    private bool _tabelGegarandeerd;

    public TableStorageNoodmailThrottleStore(string storageConnectionString, ILogger<TableStorageNoodmailThrottleStore> log)
    {
        _tableClient = new TableClient(storageConnectionString, TableName);
        _log = log;
    }

    public async Task<DateTime?> LaatsteKeerVerstuurdAsync(string sleutel)
    {
        await ZorgVoorTabelAsync();

        try
        {
            var response = await _tableClient.GetEntityAsync<TableEntity>(PartitionKey, sleutel);
            var waarde = response.Value.GetDateTimeOffset(VerstuurdOpUtcProperty);
            return waarde?.UtcDateTime;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task RegistreerVerstuurdAsync(string sleutel, DateTime verstuurdOpUtc)
    {
        await ZorgVoorTabelAsync();

        var entity = new TableEntity(PartitionKey, sleutel)
        {
            { VerstuurdOpUtcProperty, new DateTimeOffset(DateTime.SpecifyKind(verstuurdOpUtc, DateTimeKind.Utc)) }
        };
        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    public async Task WisAsync(string sleutel)
    {
        await ZorgVoorTabelAsync();

        try
        {
            await _tableClient.DeleteEntityAsync(PartitionKey, sleutel);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Al gewist, of nooit geregistreerd geweest — niets te doen.
        }
    }

    /// <summary>
    /// Maakt de tabel aan bij de eerste aanroep als die nog niet bestaat. Eén keer per proceslevensduur
    /// is genoeg; <c>CreateIfNotExistsAsync</c> is zelf al idempotent, dit voorkomt alleen een
    /// onnodige call bij elke throttle-check.
    /// </summary>
    private async Task ZorgVoorTabelAsync()
    {
        if (_tabelGegarandeerd) return;

        try
        {
            await _tableClient.CreateIfNotExistsAsync();
        }
        catch (RequestFailedException ex)
        {
            _log.LogError(ex, "Kon NoodmailThrottle-tabel niet aanmaken/verifiëren in Azure Table Storage");
            throw;
        }

        _tabelGegarandeerd = true;
    }
}
