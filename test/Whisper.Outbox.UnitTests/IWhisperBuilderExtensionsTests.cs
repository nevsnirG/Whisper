using Whisper.Abstractions;
using Whisper.Outbox.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    // Runs the real worker on the graph two AddOutbox calls build: the second call moves the first call's outbox
    // dispatcher in among the inner dispatchers, which the worker must skip rather than write the event back.
    [Fact]
    public async Task AddOutbox_CalledTwiceAfterAnotherDispatcher_WorkerDispatchesToThatDispatcherOnce()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var outboxStore = Substitute.For<IOutboxStore>();
        serviceCollection.AddSingleton(outboxStore);
        serviceCollection.AddSingleton(Substitute.For<IInstallOutbox>());
        var hostDispatcher = Substitute.For<IDispatchDomainEvents>();
        serviceCollection.AddSingleton(hostDispatcher);
        serviceCollection.AddWhisper(b =>
        {
            b.AddOutbox(_ => { });
            b.AddOutbox(_ => { });
        });
        await using var serviceProvider = serviceCollection.BuildServiceProvider();

        var domainEvent = new WorkerTestEvent("dispatched once");
        var record = new OutboxRecord
        {
            Id = Guid.NewGuid(),
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            AssemblyQualifiedType = typeof(WorkerTestEvent).AssemblyQualifiedName!,
            Payload = serviceProvider.GetRequiredService<IDomainEventSerializer>().Serialize(domainEvent),
        };
        using var stopping = new CancellationTokenSource();
        outboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([record])
            .AndDoes(_ => stopping.Cancel());
        serviceProvider.GetRequiredService<OutboxInstallerAwaiter>().SignalCompletion();
        var worker = serviceProvider.GetServices<IHostedService>().OfType<OutboxWorker>().Single();

        await worker.StartAsync(stopping.Token);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        await hostDispatcher.Received(1).Dispatch(Arg.Is<IDomainEvent>(e => e.Equals(domainEvent)), Arg.Any<CancellationToken>());
        await outboxStore.DidNotReceive().Add(Arg.Any<OutboxRecord>(), Arg.Any<CancellationToken>());
        await outboxStore.Received(1).SetDispatchedAt(record, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    private sealed record WorkerTestEvent(string Value) : IDomainEvent;

    // b.AddMediatR() (or any dispatcher) on either side of b.AddOutbox(...) ends up behind the outbox: the request
    // path dispatches to the outbox only, and the worker delivers to the dispatcher.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddOutbox_BeforeOrAfterAnotherDispatcher_PutsThatDispatcherBehindTheOutbox(bool outboxFirst)
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddSingleton(Substitute.For<IOutboxStore>());
        serviceCollection.AddWhisper(b =>
        {
            if (outboxFirst)
            {
                b.AddOutbox(_ => { });
                b.Services.AddScoped<IDispatchDomainEvents, FakeDispatcher>();
            }
            else
            {
                b.Services.AddScoped<IDispatchDomainEvents, FakeDispatcher>();
                b.AddOutbox(_ => { });
            }
        });

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        scope.ServiceProvider.GetServices<IDispatchDomainEvents>()
            .Should().ContainSingle().Which.Should().BeOfType<BlockingOutboxDispatcher>();
        scope.ServiceProvider.GetKeyedServices<IDispatchDomainEvents>(ServiceKeys.InnerDispatcher)
            .Should().ContainSingle().Which.Should().BeOfType<FakeDispatcher>();
    }

    [Fact]
    public void AddOutbox_DispatcherAddedInALaterAddWhisperCall_PutsThatDispatcherBehindTheOutbox()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddSingleton(Substitute.For<IOutboxStore>());
        serviceCollection.AddWhisper(b => b.AddOutbox(_ => { }));
        serviceCollection.AddWhisper(b => b.Services.AddScoped<IDispatchDomainEvents, FakeDispatcher>());

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        scope.ServiceProvider.GetServices<IDispatchDomainEvents>()
            .Should().ContainSingle().Which.Should().BeOfType<BlockingOutboxDispatcher>();
        scope.ServiceProvider.GetKeyedServices<IDispatchDomainEvents>(ServiceKeys.InnerDispatcher)
            .Should().ContainSingle().Which.Should().BeOfType<FakeDispatcher>();
    }

    [Fact]
    public void AddOutbox_CalledTwice_RegistersTheOutboxDispatcherOnce()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddSingleton(Substitute.For<IOutboxStore>());
        serviceCollection.AddWhisper(b =>
        {
            b.AddOutbox(_ => { });
            b.AddOutbox(_ => { });
        });

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        scope.ServiceProvider.GetServices<IDispatchDomainEvents>().Should().ContainSingle();
        scope.ServiceProvider.GetKeyedServices<IDispatchDomainEvents>(ServiceKeys.InnerDispatcher).Should().BeEmpty();
    }

    private sealed class FakeDispatcher : IDispatchDomainEvents
    {
        public Task Dispatch(IDomainEvent domainEvent, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Dispatch(IDomainEvent[] domainEvents, CancellationToken cancellationToken) => Task.CompletedTask;
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
    [InlineData(10, -1, nameof(OutboxWorkerOptions.PollingIntervalMs))]
    [InlineData(10, -2, nameof(OutboxWorkerOptions.PollingIntervalMs))]
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConfigureWorker_NonPositiveRetention_FailsValidation(int retentionMinutes)
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddWhisper(b => b.AddOutbox(o => o.ConfigureWorker(w => w.RetainDispatchedFor = TimeSpan.FromMinutes(retentionMinutes))));

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        var act = () => serviceProvider.GetRequiredService<IOptions<OutboxWorkerOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(nameof(OutboxWorkerOptions.RetainDispatchedFor));
    }

    [Fact]
    public void ConfigureWorker_PositiveRetention_PassesValidation()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddWhisper(b => b.AddOutbox(o => o.ConfigureWorker(w => w.RetainDispatchedFor = TimeSpan.FromDays(7))));

        using var serviceProvider = serviceCollection.BuildServiceProvider();

        serviceProvider.GetRequiredService<IOptions<OutboxWorkerOptions>>().Value.RetainDispatchedFor.Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public void ConfigureWorker_ZeroPollingInterval_PassesValidation()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddOptions();
        serviceCollection.AddWhisper(b => b.AddOutbox(o => o.ConfigureWorker(w => w.PollingIntervalMs = 0)));

        using var serviceProvider = serviceCollection.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<OutboxWorkerOptions>>().Value;

        options.PollingIntervalMs.Should().Be(0, "a 0 ms interval polls continuously, which is wasteful but works");
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