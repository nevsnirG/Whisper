using Hermes.Core;
using MediatR;

public class SomeDomainEvent : IDomainEvent, INotification
{
    public required string SomeProperty { get; init; }
}
