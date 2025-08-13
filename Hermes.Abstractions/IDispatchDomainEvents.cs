using Hermes.Core;

namespace Hermes.Abstractions;

public interface IDispatchDomainEvents
{
    Task Dispatch(IDomainEvent domainEvent);

    Task Dispatch(IDomainEvent[] domainEvents);
}