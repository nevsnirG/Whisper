using Whisper.Abstractions;
using Whisper.Outbox.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

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
    public void AddOutbox_WithKeyedDispatcherRegistered_LeavesItUntouched()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        var hostDispatcher = Substitute.For<IDispatchDomainEvents>();
        serviceCollection.AddKeyedSingleton("host-key", hostDispatcher);

        var act = () => serviceCollection.AddWhisper(b => b.AddOutbox(_ => { }));

        act.Should().NotThrow();
        using var serviceProvider = serviceCollection.BuildServiceProvider();
        serviceProvider.GetRequiredKeyedService<IDispatchDomainEvents>("host-key").Should().BeSameAs(hostDispatcher);
        serviceProvider.GetKeyedServices<IDispatchDomainEvents>(ServiceKeys.InnerDispatcher).Should().BeEmpty();
    }

    [Fact]
    public void AddOutbox_CalledTwiceAfterAnotherDispatcher_KeepsThatDispatcherAsSingleInnerDispatcher()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddSingleton(Substitute.For<IOutboxStore>());
        var innerDispatcher = Substitute.For<IDispatchDomainEvents>();
        serviceCollection.AddSingleton(innerDispatcher);

        var act = () => serviceCollection.AddWhisper(b =>
        {
            b.AddOutbox(_ => { });
            b.AddOutbox(_ => { });
        });

        act.Should().NotThrow();
        using var serviceProvider = serviceCollection.BuildServiceProvider();
        serviceProvider.GetRequiredService<OutboxInstallerAwaiter>().SignalCompletion();
        // The worker skips OutboxDispatcher, so what it actually dispatches to is the host's dispatcher, once.
        serviceProvider.GetKeyedServices<IDispatchDomainEvents>(ServiceKeys.InnerDispatcher)
            .Where(d => d is not OutboxDispatcher)
            .Should().ContainSingle().Which.Should().BeSameAs(innerDispatcher);
    }

    [Fact]
    public void AddOutbox_WhenHostRegisteredTimeProvider_DoesNotOverrideIt()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        var fakeTimeProvider = new FakeTimeProvider();
        serviceCollection.AddSingleton<TimeProvider>(fakeTimeProvider);
        serviceCollection.AddWhisper(b => b.AddOutbox(_ => { }));

        serviceCollection.Should().ContainSingle(r => r.ServiceType == typeof(TimeProvider));

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        serviceProvider.GetRequiredService<TimeProvider>().Should().BeSameAs(fakeTimeProvider);
    }

    [Fact]
    public void AddOutbox_WhenNoTimeProviderRegistered_RegistersSystemTimeProvider()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddWhisper(b => b.AddOutbox(_ => { }));

        serviceCollection.Should().ContainSingle(r => r.ServiceType == typeof(TimeProvider));

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        serviceProvider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
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

    [Theory]
    [InlineData(0, 1000, nameof(OutboxWorkerOptions.BatchSize))]
    [InlineData(-1, 1000, nameof(OutboxWorkerOptions.BatchSize))]
    [InlineData(10, 0, nameof(OutboxWorkerOptions.PollingIntervalMs))]
    [InlineData(10, -1, nameof(OutboxWorkerOptions.PollingIntervalMs))]
    public void ConfigureWorker_InvalidOptions_FailValidation(int batchSize, int pollingIntervalMs, string invalidOption)
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddWhisper(b => b.AddOutbox(o => o.ConfigureWorker(w =>
        {
            w.BatchSize = batchSize;
            w.PollingIntervalMs = pollingIntervalMs;
        })));

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        var act = () => serviceProvider.GetRequiredService<IOptions<OutboxWorkerOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(invalidOption);
    }

    [Fact]
    public void ConfigureWorker_NotCalled_DefaultsPassValidation()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddWhisper(b => b.AddOutbox(_ => { }));

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<OutboxWorkerOptions>>().Value;

        options.BatchSize.Should().Be(10);
        options.PollingIntervalMs.Should().Be(1000);
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