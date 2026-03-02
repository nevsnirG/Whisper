using System.Collections.Concurrent;
using MediatR;

namespace Whisper.Outbox.IntegrationTests.Fakes;

public sealed class TestDomainEventHandler : INotificationHandler<TestDomainEvent>
{
    private readonly ConcurrentBag<TestDomainEvent> _receivedEvents = [];
    private readonly TaskCompletionSource<TestDomainEvent> _firstEventReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyCollection<TestDomainEvent> ReceivedEvents => _receivedEvents;

    public Task WaitForFirstEvent(TimeSpan timeout)
        => _firstEventReceived.Task.WaitAsync(timeout);

    public Task Handle(TestDomainEvent notification, CancellationToken cancellationToken)
    {
        _receivedEvents.Add(notification);
        _firstEventReceived.TrySetResult(notification);
        return Task.CompletedTask;
    }
}
