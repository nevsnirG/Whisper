﻿namespace Whisper.Abstractions;

public interface IDispatchDomainEvents
{
    Task Dispatch(IDomainEvent domainEvent, CancellationToken cancellationToken);

    Task Dispatch(IDomainEvent[] domainEvents, CancellationToken cancellationToken);
}