using Whisper.Abstractions;
using Whisper.Outbox.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Whisper.Outbox.UnitTests;

public class IWhisperBuilderExtensionsTests
{
    [Fact]
    public void AddOutbox_WhenAwaitIsNotReady_RegistersBlockingDispatcher()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddWhisper(b => b.AddOutbox(b => { }));
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
        serviceCollection.AddOptions();
        serviceCollection.AddWhisper(b => b.AddOutbox(b => { }));
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

    [Fact]
    public void ConfigureRecoverability_BindsAllOptions()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        Func<int, TimeSpan> retryDelay = attempt => TimeSpan.FromSeconds(attempt);
        Func<Exception, bool> predicate = _ => true;
        serviceCollection.AddWhisper(b => b.AddOutbox(o => o.ConfigureRecoverability(r =>
        {
            r.MaxRetries = 7;
            r.RetryDelay = retryDelay;
            r.UnrecoverableExceptionTypes.Add(typeof(InvalidOperationException));
            r.UnrecoverableExceptionPredicates.Add(predicate);
        })));

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<OutboxRecoverabilityOptions>>().Value;

        options.MaxRetries.Should().Be(7);
        options.RetryDelay.Should().BeSameAs(retryDelay);
        options.UnrecoverableExceptionTypes.Should().Equal(typeof(InvalidOperationException));
        options.UnrecoverableExceptionPredicates.Should().Equal(predicate);
    }

    [Fact]
    public void ConfigureRecoverability_NotCalled_UsesDefaults()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddWhisper(b => b.AddOutbox(_ => { }));

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<OutboxRecoverabilityOptions>>().Value;

        options.MaxRetries.Should().Be(3);
        options.RetryDelay.Should().BeNull();
        options.UnrecoverableExceptionTypes.Should().BeEmpty();
        options.UnrecoverableExceptionPredicates.Should().BeEmpty();
    }
}