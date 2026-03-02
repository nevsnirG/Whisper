using MediatR;
using Whisper;

namespace Whisper.Outbox.IntegrationTests.Fakes;

public sealed record TestDomainEvent(string Value) : IDomainEvent, INotification;
