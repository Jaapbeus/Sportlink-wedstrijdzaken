using Azure.Core;
using Azure.Storage.Queues;
using AwesomeAssertions;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

public class OpslagVerbindingTests
{
    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken t) => new("x", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken t) => new(GetToken(c, t));
    }

    private static Func<string, string?> Instellingen(params (string k, string v)[] items)
    {
        var d = items.ToDictionary(i => i.k, i => i.v);
        return k => d.TryGetValue(k, out var v) ? v : null;
    }

    private static readonly QueueClientOptions Opties = new() { MessageEncoding = QueueMessageEncoding.Base64 };

    [Fact]
    public void ConnectionString_Wint_VanAccountName()
    {
        var i = Instellingen(("AzureWebJobsStorage", "UseDevelopmentStorage=true"), ("AzureWebJobsStorage__accountName", "acc"));
        var t = OpslagVerbinding.MaakTableClient("tab", i);
        t.Uri.Host.Should().NotContain("acc.table");
        t.Name.Should().Be("tab");
        OpslagVerbinding.MaakQueueClient("q", Opties, i).Name.Should().Be("q");
    }

    [Fact]
    public void AccountName_GeeftCorrecteHostEnNamen()
    {
        var i = Instellingen(("AzureWebJobsStorage__accountName", "stacc"));
        var t = OpslagVerbinding.MaakTableClient("NoodmailThrottle", i, new FakeCredential());
        t.Uri.Host.Should().Be("stacc.table.core.windows.net");
        t.Name.Should().Be("NoodmailThrottle");
        var q = OpslagVerbinding.MaakQueueClient("sync-jobs", Opties, i, new FakeCredential());
        q.Uri.Host.Should().Be("stacc.queue.core.windows.net");
        q.Name.Should().Be("sync-jobs");
    }

    [Fact]
    public void ExpliciteServiceUris_WinnenVanAccountName()
    {
        var i = Instellingen(("AzureWebJobsStorage__accountName", "stacc"),
            ("AzureWebJobsStorage__queueServiceUri", "https://eigen.queue.core.windows.net"));
        OpslagVerbinding.MaakQueueClient("q", Opties, i, new FakeCredential()).Uri.Host.Should().Be("eigen.queue.core.windows.net");
    }

    [Fact]
    public void ClientId_GeeftManagedIdentity()
    {
        var i = Instellingen(("AzureWebJobsStorage__accountName", "a"), ("AzureWebJobsStorage__clientId", "11111111-1111-1111-1111-111111111111"));
        OpslagVerbinding.GebruiktManagedIdentityMetClientId(i).Should().BeTrue();
        OpslagVerbinding.MaakCredential(i).GetType().Name.Should().Be("ManagedIdentityCredential");
        OpslagVerbinding.MaakCredential(Instellingen(("AzureWebJobsStorage__accountName", "a"))).GetType().Name.Should().Be("DefaultAzureCredential");
    }

    [Fact]
    public void NietsGeconfigureerd_GooitHelderBericht()
    {
        var act = () => OpslagVerbinding.MaakTableClient("t", Instellingen());
        act.Should().Throw<InvalidOperationException>().WithMessage("*AzureWebJobsStorage*AzureWebJobsStorage__accountName*");
        var act2 = () => OpslagVerbinding.MaakQueueClient("q", Opties, Instellingen());
        act2.Should().Throw<InvalidOperationException>();
    }
}
