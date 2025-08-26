using MediatR;

public class SomeDomainEventHandler : INotificationHandler<SomeDomainEvent>
{
    public Task Handle(SomeDomainEvent notification, CancellationToken cancellationToken)
    {
        Console.WriteLine(notification.SomeProperty);
        return Task.CompletedTask;
    }
}
