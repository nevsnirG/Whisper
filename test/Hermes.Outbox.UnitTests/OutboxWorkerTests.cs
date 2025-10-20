using Hermes.Abstractions;
using Hermes.Outbox.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Hermes.Outbox.UnitTests;
public class OutboxWorkerTests
{
    [Fact]
    public async Task WhenOutboxEmpty_DoesNotDispatchAnything()
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var outboxStore = Substitute.For<IOutboxStore>();
        outboxStore.ReadNextBatch(cancellationTokenSource.Token)
            .Returns([])
            .AndDoes(_ => cancellationTokenSource.Cancel());
        var serializer = Substitute.For<IDomainEventSerializer>();
        var timeProvider = new FakeTimeProvider();
        var awaiter = new OutboxInstallerAwaiter();
        awaiter.SignalCompletion();
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        var sut = new OutboxWorker(outboxStore, serializer, timeProvider, awaiter, serviceScopeFactory);

        await sut.StartAsync(cancellationTokenSource.Token);

        await outboxStore.ReceivedWithAnyArgs(1).ReadNextBatch(default);
        serviceScopeFactory.ReceivedCalls().Should().BeEmpty();
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
        outboxStore.ReadNextBatch(Arg.Any<CancellationToken>())
            .Returns([outboxRecord])
            .AndDoes(_ => cancellationTokenSource.Cancel());
        var serializer = Substitute.For<IDomainEventSerializer>();
        serializer.Deserialize("SomePayload", "SomeType")
            .Returns(domainEvent);
        var timeProvider = new FakeTimeProvider();
        var awaiter = new OutboxInstallerAwaiter();
        awaiter.SignalCompletion();
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScopeFactory.CreateScope()
            .Returns(serviceScope);
        var keyedServiceProvider = Substitute.For<IKeyedServiceProvider>();
        serviceScope.ServiceProvider.Returns(keyedServiceProvider);
        keyedServiceProvider.GetRequiredKeyedService(Arg.Any<Type>(), "innerDispatcher")
            .Returns(Array.Empty<IDispatchDomainEvents>());
        var sut = new OutboxWorker(outboxStore, serializer, timeProvider, awaiter, serviceScopeFactory);

        await sut.StartAsync(cancellationTokenSource.Token);

        await outboxStore.ReceivedWithAnyArgs(1).ReadNextBatch(default);
        serializer.Received(1).Deserialize("SomePayload", "SomeType");
        serviceScopeFactory.Received(1).CreateScope();
        await outboxStore.Received(1).SetDispatchedAt(Arg.Is<OutboxRecord[]>(ors =>
            ors.Single().DispatchedAtUtc == timeProvider.GetUtcNow()), Arg.Any<CancellationToken>());
    }

    private record TestEvent() : IDomainEvent;
}
