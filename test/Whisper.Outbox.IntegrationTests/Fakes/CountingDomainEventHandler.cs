using System.Collections.Concurrent;
using MediatR;

namespace Whisper.Outbox.IntegrationTests.Fakes;

/// <summary>
/// Counts how often each event (by its <see cref="TestDomainEvent.Value"/>) is handled. One instance is shared by
/// every host of a test, so a count above one means two workers dispatched the same record.
/// </summary>
public sealed class CountingDomainEventHandler : INotificationHandler<TestDomainEvent>
{
    private readonly ConcurrentDictionary<string, int> _timesHandled = new();

    public IReadOnlyDictionary<string, int> TimesHandled => _timesHandled;

    public async Task Handle(TestDomainEvent notification, CancellationToken cancellationToken)
    {
        _timesHandled.AddOrUpdate(notification.Value, 1, (_, count) => count + 1);

        // Long enough for another worker to read the same record before this one is marked as dispatched.
        await Task.Delay(TimeSpan.FromMilliseconds(5), CancellationToken.None);
    }
}
