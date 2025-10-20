using Hermes.Abstractions;
using Hermes.Outbox.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Hermes.Outbox.UnitTests;

public class IHermesBuilderExtensionsTests
{
    [Fact]
    public void AddOutbox_WhenAwaitIsNotReady_RegistersBlockingDispatcher()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddHermes(b => b.AddOutbox(b => { }));
        serviceCollection.AddSingleton(Substitute.For<IOutboxStore>());

        serviceCollection.Should()
            .ContainSingle(r => r.ServiceType == typeof(IDispatchDomainEvents))
            .Which.ImplementationFactory.Should().NotBeNull();

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        var dispatcher = serviceProvider.GetRequiredService<IDispatchDomainEvents>();
        dispatcher.Should().BeOfType<BlockingOutboxDispatcher>();
    }

    [Fact]
    public void AddOutbox_WhenAwaiterReady_RegistersNonBlockingDispatcher()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddHermes(b => b.AddOutbox(b => { }));
        serviceCollection.AddSingleton(Substitute.For<IOutboxStore>());

        serviceCollection.Should()
            .ContainSingle(r => r.ServiceType == typeof(IDispatchDomainEvents))
            .Which.ImplementationFactory.Should().NotBeNull();

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        var awaiter = serviceProvider.GetRequiredService<OutboxInstallerAwaiter>();
        awaiter.SignalCompletion();

        var dispatcher = serviceProvider.GetRequiredService<IDispatchDomainEvents>();
        dispatcher.Should().BeOfType<OutboxDispatcher>();
    }
}