using Hermes.Outbox.Abstractions;
using Microsoft.Extensions.Time.Testing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hermes.Outbox.UnitTests;
public class OutboxDispatcherTests
{
    [Fact]
    public async Task GivenDomainEvent_CreatesAndPersistsOutboxRecord()
    {
        var outboxStore = Substitute.For<IOutboxStore>();
        var serializer = Substitute.For<IDomainEventSerializer>();
        serializer.Serialize(Arg.Any<IDomainEvent>())
            .Returns("serialized");
        var timeProvider = new FakeTimeProvider();
        var uuidProvider = Substitute.For<IUuidProvider>();
        var guid = Guid.CreateVersion7();
        uuidProvider.Provide()
            .Returns(guid);
        var domainEvent = new TestEvent();
        var sut = new OutboxDispatcher(outboxStore, serializer, timeProvider, uuidProvider);

        await sut.Dispatch(domainEvent, CancellationToken.None);

        await outboxStore.Received(1).Add(Arg.Is<OutboxRecord>(or =>
            or.Payload == "serialized"
         && or.EnqueuedAtUtc == timeProvider.GetUtcNow()
         && or.DispatchedAtUtc == null
         && or.Id == guid
         && or.AssemblyQualifiedType == typeof(TestEvent).AssemblyQualifiedName), CancellationToken.None);
    }

    [Fact]
    public async Task GivenDomainEvents_CreatesAndPersistsOutboxRecord()
    {
        var outboxStore = Substitute.For<IOutboxStore>();
        var serializer = Substitute.For<IDomainEventSerializer>();
        serializer.Serialize(Arg.Any<IDomainEvent>())
            .Returns("serialized");
        var timeProvider = new FakeTimeProvider();
        var uuidProvider = new DefaultUuidProvider();
        var sut = new OutboxDispatcher(outboxStore, serializer, timeProvider, uuidProvider);

        await sut.Dispatch([new TestEvent(), new TestEvent()], CancellationToken.None);

        await outboxStore.Received(1).Add(Arg.Any<OutboxRecord[]>(), CancellationToken.None);
    }

    private record TestEvent() : IDomainEvent;
}
