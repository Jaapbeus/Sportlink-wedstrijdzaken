using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Queues;

namespace Planner.Shared.Infrastructure;

/// <summary>
/// Bouwt Table- en Queue-clients op het <c>AzureWebJobsStorage</c>-opslagaccount (#1512).
/// Volgorde: (1) connection string <c>AzureWebJobsStorage</c> (Linux Consumption, Azurite lokaal);
/// (2) identity-based <c>AzureWebJobsStorage__accountName</c> (Flex Consumption, managed identity),
/// met optioneel <c>__clientId</c>, <c>__tableServiceUri</c> en <c>__queueServiceUri</c>;
/// (3) anders een duidelijke <see cref="InvalidOperationException"/>.
/// Tier-onafhankelijk, dus hier en niet in een tierproject.
/// </summary>
public static class OpslagVerbinding
{
    private const string Basis = "AzureWebJobsStorage";

    public static TableClient MaakTableClient(string tabelNaam, Func<string, string?>? instelling = null, TokenCredential? credential = null)
    {
        instelling ??= Environment.GetEnvironmentVariable;
        var verbinding = Waarde(instelling, Basis);
        if (verbinding is not null) return new TableClient(verbinding, tabelNaam);

        var uri = TabelServiceUri(instelling);
        return new TableClient(uri, tabelNaam, credential ?? MaakCredential(instelling));
    }

    public static QueueClient MaakQueueClient(string queueNaam, QueueClientOptions options, Func<string, string?>? instelling = null, TokenCredential? credential = null)
    {
        instelling ??= Environment.GetEnvironmentVariable;
        var verbinding = Waarde(instelling, Basis);
        if (verbinding is not null) return new QueueClient(verbinding, queueNaam, options);

        var uri = QueueServiceUri(instelling);
        return new QueueClient(new Uri(uri, queueNaam), credential ?? MaakCredential(instelling), options);
    }

    internal static Uri TabelServiceUri(Func<string, string?> instelling)
        => ServiceUri(instelling, "__tableServiceUri", "table");

    internal static Uri QueueServiceUri(Func<string, string?> instelling)
        => ServiceUri(instelling, "__queueServiceUri", "queue");

    private static Uri ServiceUri(Func<string, string?> instelling, string uriSuffix, string dienst)
    {
        var expliciet = Waarde(instelling, Basis + uriSuffix);
        if (expliciet is not null) return MetSlash(expliciet);

        var account = Waarde(instelling, Basis + "__accountName")
            ?? throw new InvalidOperationException(
                "Opslag niet geconfigureerd — stel AzureWebJobsStorage (connection string) of " +
                "AzureWebJobsStorage__accountName (identity-based, Flex Consumption) in.");
        return new Uri($"https://{account}.{dienst}.core.windows.net/");
    }

    internal static TokenCredential MaakCredential(Func<string, string?> instelling)
    {
        var clientId = Waarde(instelling, Basis + "__clientId");
        return clientId is null
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    }

    internal static bool GebruiktManagedIdentityMetClientId(Func<string, string?> instelling)
        => Waarde(instelling, Basis + "__clientId") is not null;

    private static Uri MetSlash(string uri) => new(uri.EndsWith('/') ? uri : uri + "/");

    private static string? Waarde(Func<string, string?> instelling, string sleutel)
    {
        var w = instelling(sleutel);
        return string.IsNullOrWhiteSpace(w) ? null : w;
    }
}
