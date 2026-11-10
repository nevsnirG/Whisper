using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Whisper.Outbox;

internal interface IDomainEventSerializer
{
    string Serialize(IDomainEvent domainEvent);

    /// <exception cref="UnknownDomainEventTypeException">The type cannot be loaded by this application.</exception>
    IDomainEvent Deserialize(string json, string assemblyQualifiedType);
}

internal sealed class DomainEventSerializer(IOptions<OutboxJsonOptions> outboxJsonOptions) : IDomainEventSerializer
{
    private readonly JsonSerializerOptions _jsonSerializerOptions = outboxJsonOptions.Value.SerializerOptions;

    public string Serialize(IDomainEvent domainEvent)
    {
        return JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), _jsonSerializerOptions);
    }

    public IDomainEvent Deserialize(string json, string assemblyQualifiedType)
    {
        var type = DomainEventTypeName.Resolve(assemblyQualifiedType);

        if (!type.IsAssignableTo(typeof(IDomainEvent)))
        {
            throw new InvalidOperationException($"{assemblyQualifiedType} is not assignable to {typeof(IDomainEvent).AssemblyQualifiedName}.");
        }

        return JsonSerializer.Deserialize(json, type, _jsonSerializerOptions) as IDomainEvent
            ?? throw new JsonException($"The payload of a {assemblyQualifiedType} record deserialized to null.");
    }
}
