namespace Hermes.Outbox;
public class OutboxRecord
{
    public required Guid Id { get; init; }
    public DateTimeOffset? DispatchedAtUtc { get; set; }
    public required string AssemblyQualifiedType { get; init; }
    public required string Payload { get; init; }
}