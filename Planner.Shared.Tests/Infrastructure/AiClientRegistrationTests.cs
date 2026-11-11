using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

public class AiClientRegistrationTests
{
    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void TryRegister_BlankModel_DoesNotCallProviderFactoryOrRegisterClient()
    {
        var services = new ServiceCollection();
        var factoryCalls = 0;

        var registered = AiClientRegistration.TryRegister(
            services, "synthetic-test-key", "  ", externalIntegrationsAllowed: true,
            (_, _) => { factoryCalls++; return new FakeChatClient(); });

        registered.Should().BeFalse();
        factoryCalls.Should().Be(0);
        services.Should().NotContain(d => d.ServiceType == typeof(IChatClient));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("synthetic-test-key", false)]
    public void TryRegister_MissingKeyOrBlockedEgress_DoesNotCallProviderFactory(string? key, bool egressAllowed)
    {
        var services = new ServiceCollection();
        var factoryCalls = 0;

        var registered = AiClientRegistration.TryRegister(
            services, key, "synthetic-test-model", egressAllowed,
            (_, _) => { factoryCalls++; return new FakeChatClient(); });

        registered.Should().BeFalse();
        factoryCalls.Should().Be(0);
        services.Should().NotContain(d => d.ServiceType == typeof(IChatClient));
    }

    [Fact]
    public void TryRegister_CompleteConfiguration_UsesConfiguredValuesAndRegistersClient()
    {
        var services = new ServiceCollection();
        var client = new FakeChatClient();
        string? suppliedModel = null;
        string? suppliedKey = null;

        var registered = AiClientRegistration.TryRegister(
            services, "synthetic-test-key", "synthetic-test-model", externalIntegrationsAllowed: true,
            (model, key) =>
            {
                suppliedModel = model;
                suppliedKey = key;
                return client;
            });

        registered.Should().BeTrue();
        suppliedModel.Should().Be("synthetic-test-model");
        suppliedKey.Should().Be("synthetic-test-key");
        services.Should().ContainSingle(d => d.ServiceType == typeof(IChatClient))
            .Which.ImplementationInstance.Should().BeSameAs(client);
    }
}
