using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Whisper.Abstractions;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.UnitTests;

public class OutboxWorkerTests
{
    [Fact]
    public async Task WhenOutboxEmpty_DoesNotDispatchAnything()
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var outboxStore = Substitute.For<IOutboxStore>();
        outboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([])
            .AndDoes(_ => cancellationTokenSource.Cancel());
        var serializer = Substitute.For<IDomainEventSerializer>();
        var timeProvider = new FakeTimeProvider();
        var awaiter = new OutboxInstallerAwaiter();
        awaiter.SignalCompletion();
        var options = Options.Create(new OutboxWorkerOptions());
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScopeFactory.CreateScope().Returns(serviceScope);
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceScope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IOutboxStore)).Returns(outboxStore);
        var logger = Substitute.For<ILogger<OutboxWorker>>();
        var sut = new OutboxWorker(serializer, timeProvider, awaiter, options, serviceScopeFactory, logger);

        await sut.StartAsync(cancellationTokenSource.Token);

        await outboxStore.ReceivedWithAnyArgs(1).ReadNextBatch(Arg.Any<int>(), default);
    }

    [Fact]
    public async Task WhenOutboxNotEmpty_DispatchesRecords_AndMarksAsDispatched()
    {
        var outboxRecord = new OutboxRecord()
        {
            Id = Guid.NewGuid(),
            AssemblyQualifiedType = "SomeType",
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            DispatchedAtUtc = null,
            Payload = "SomePayload"
        };
        var domainEvent = new TestEvent();
        var cancellationTokenSource = new CancellationTokenSource();
        var outboxStore = Substitute.For<IOutboxStore>();
        outboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxRecord])
            .AndDoes(_ => cancellationTokenSource.Cancel());
        var serializer = Substitute.For<IDomainEventSerializer>();
        serializer.Deserialize("SomePayload", "SomeType")
            .Returns(domainEvent);
        var timeProvider = new FakeTimeProvider();
        var awaiter = new OutboxInstallerAwaiter();
        awaiter.SignalCompletion();
        var options = Options.Create(new OutboxWorkerOptions());
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScopeFactory.CreateScope()
            .Returns(serviceScope);
        var keyedServiceProvider = Substitute.For<IKeyedServiceProvider>();
        serviceScope.ServiceProvider.Returns(keyedServiceProvider);
        keyedServiceProvider.GetService(typeof(IOutboxStore)).Returns(outboxStore);
        keyedServiceProvider.GetRequiredKeyedService(Arg.Any<Type>(), "innerDispatcher")
            .Returns(Array.Empty<IDispatchDomainEvents>());
        var logger = Substitute.For<ILogger<OutboxWorker>>();
        var sut = new OutboxWorker(serializer, timeProvider, awaiter, options, serviceScopeFactory, logger);

        await sut.StartAsync(cancellationTokenSource.Token);

        await outboxStore.ReceivedWithAnyArgs(1).ReadNextBatch(Arg.Any<int>(), default);
        serializer.Received(1).Deserialize("SomePayload", "SomeType");
        await outboxStore.Received(1).SetDispatchedAt(Arg.Any<OutboxRecord>(), timeProvider.GetUtcNow(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenDeserializationFails_MarksRecordAsFailed_AndContinues()
    {
        var outboxRecord1 = new OutboxRecord
        {
            Id = Guid.NewGuid(),
            AssemblyQualifiedType = "BadType",
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            Payload = "BadPayload"
        };
        var outboxRecord2 = new OutboxRecord
        {
            Id = Guid.NewGuid(),
            AssemblyQualifiedType = "GoodType",
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            Payload = "GoodPayload"
        };
        var domainEvent = new TestEvent();
        var cancellationTokenSource = new CancellationTokenSource();
        var outboxStore = Substitute.For<IOutboxStore>();
        outboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxRecord1, outboxRecord2])
            .AndDoes(_ => cancellationTokenSource.Cancel());
        var serializer = Substitute.For<IDomainEventSerializer>();
        serializer.When(s => s.Deserialize("BadPayload", "BadType"))
            .Do(_ => throw new InvalidOperationException("Bad type"));
        serializer.Deserialize("GoodPayload", "GoodType")
            .Returns(domainEvent);
        var timeProvider = new FakeTimeProvider();
        var awaiter = new OutboxInstallerAwaiter();
        awaiter.SignalCompletion();
        var options = Options.Create(new OutboxWorkerOptions());
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScopeFactory.CreateScope().Returns(serviceScope);
        var keyedServiceProvider = Substitute.For<IKeyedServiceProvider>();
        serviceScope.ServiceProvider.Returns(keyedServiceProvider);
        keyedServiceProvider.GetService(typeof(IOutboxStore)).Returns(outboxStore);
        keyedServiceProvider.GetRequiredKeyedService(Arg.Any<Type>(), "innerDispatcher")
            .Returns(Array.Empty<IDispatchDomainEvents>());
        var logger = Substitute.For<ILogger<OutboxWorker>>();
        var sut = new OutboxWorker(serializer, timeProvider, awaiter, options, serviceScopeFactory, logger);

        await sut.StartAsync(cancellationTokenSource.Token);

        // Deserialization failure → dead-lettered immediately
        await outboxStore.Received(1).SetFailedAt(outboxRecord1, timeProvider.GetUtcNow(), Arg.Any<CancellationToken>());
        // Second record still processed successfully
        await outboxStore.Received(1).SetDispatchedAt(outboxRecord2, timeProvider.GetUtcNow(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenDispatchFails_AndRetriesNotExhausted_IncrementsRetries()
    {
        var outboxRecord = new OutboxRecord
        {
            Id = Guid.NewGuid(),
            AssemblyQualifiedType = "SomeType",
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            Retries = 0,
            Payload = "SomePayload"
        };
        var domainEvent = new TestEvent();
        var cancellationTokenSource = new CancellationTokenSource();
        var outboxStore = Substitute.For<IOutboxStore>();
        outboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxRecord])
            .AndDoes(_ => cancellationTokenSource.Cancel());
        var serializer = Substitute.For<IDomainEventSerializer>();
        serializer.Deserialize("SomePayload", "SomeType").Returns(domainEvent);
        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        dispatcher.When(d => d.Dispatch(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new Exception("Downstream service unavailable"));
        var timeProvider = new FakeTimeProvider();
        var awaiter = new OutboxInstallerAwaiter();
        awaiter.SignalCompletion();
        var options = Options.Create(new OutboxWorkerOptions { MaxRetries = 3 });
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScopeFactory.CreateScope().Returns(serviceScope);
        var keyedServiceProvider = Substitute.For<IKeyedServiceProvider>();
        serviceScope.ServiceProvider.Returns(keyedServiceProvider);
        keyedServiceProvider.GetService(typeof(IOutboxStore)).Returns(outboxStore);
        keyedServiceProvider.GetKeyedServices(typeof(IDispatchDomainEvents), "innerDispatcher")
            .Returns(new object[] { dispatcher });
        var logger = Substitute.For<ILogger<OutboxWorker>>();
        var sut = new OutboxWorker(serializer, timeProvider, awaiter, options, serviceScopeFactory, logger);

        await sut.StartAsync(cancellationTokenSource.Token);

        // Retries not exhausted → increment
        await outboxStore.Received(1).IncrementRetries(outboxRecord, Arg.Any<CancellationToken>());
        await outboxStore.DidNotReceive().SetFailedAt(outboxRecord, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenDispatchFails_AndRetriesExhausted_MarksAsFailed()
    {
        var outboxRecord = new OutboxRecord
        {
            Id = Guid.NewGuid(),
            AssemblyQualifiedType = "SomeType",
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            Retries = 2, // Already retried twice, max is 3
            Payload = "SomePayload"
        };
        var domainEvent = new TestEvent();
        var cancellationTokenSource = new CancellationTokenSource();
        var outboxStore = Substitute.For<IOutboxStore>();
        outboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([outboxRecord])
            .AndDoes(_ => cancellationTokenSource.Cancel());
        var serializer = Substitute.For<IDomainEventSerializer>();
        serializer.Deserialize("SomePayload", "SomeType").Returns(domainEvent);
        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        dispatcher.When(d => d.Dispatch(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new Exception("Downstream still down"));
        var timeProvider = new FakeTimeProvider();
        var awaiter = new OutboxInstallerAwaiter();
        awaiter.SignalCompletion();
        var options = Options.Create(new OutboxWorkerOptions { MaxRetries = 3 });
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScopeFactory.CreateScope().Returns(serviceScope);
        var keyedServiceProvider = Substitute.For<IKeyedServiceProvider>();
        serviceScope.ServiceProvider.Returns(keyedServiceProvider);
        keyedServiceProvider.GetService(typeof(IOutboxStore)).Returns(outboxStore);
        keyedServiceProvider.GetKeyedServices(typeof(IDispatchDomainEvents), "innerDispatcher")
            .Returns(new object[] { dispatcher });
        var logger = Substitute.For<ILogger<OutboxWorker>>();
        var sut = new OutboxWorker(serializer, timeProvider, awaiter, options, serviceScopeFactory, logger);

        await sut.StartAsync(cancellationTokenSource.Token);

        // Retries exhausted → dead-lettered
        await outboxStore.Received(1).SetFailedAt(outboxRecord, timeProvider.GetUtcNow(), Arg.Any<CancellationToken>());
        await outboxStore.DidNotReceive().IncrementRetries(outboxRecord, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenReadNextBatchThrows_LogsErrorAndContinues()
    {
        var callCount = 0;
        var cancellationTokenSource = new CancellationTokenSource();
        var outboxStore = Substitute.For<IOutboxStore>();
        outboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                    throw new Exception("DB connection failed");
                cancellationTokenSource.Cancel();
                return Array.Empty<OutboxRecord>();
            });
        var serializer = Substitute.For<IDomainEventSerializer>();
        var timeProvider = new FakeTimeProvider();
        var awaiter = new OutboxInstallerAwaiter();
        awaiter.SignalCompletion();
        var options = Options.Create(new OutboxWorkerOptions { PollingIntervalMs = 1 });
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScopeFactory.CreateScope().Returns(serviceScope);
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceScope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IOutboxStore)).Returns(outboxStore);
        var logger = Substitute.For<ILogger<OutboxWorker>>();
        var sut = new OutboxWorker(serializer, timeProvider, awaiter, options, serviceScopeFactory, logger);

        await sut.StartAsync(cancellationTokenSource.Token);
        await Task.Delay(200);
        await sut.StopAsync(CancellationToken.None);

        await outboxStore.Received(2).ReadNextBatch(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private record TestEvent() : IDomainEvent;
}
