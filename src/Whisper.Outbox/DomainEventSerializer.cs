using System.Text.Json;

namespace Whisper.Outbox;
internal interface IDomainEventSerializer
{
    string Serialize(IDomainEvent domainEvent);
    IDomainEvent Deserialize(string json, string assemblyQualifiedType);
}

internal sealed class DomainEventSerializer : IDomainEventSerializer
{
    private static readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        WriteIndented = false,
    };

    public string Serialize(IDomainEvent domainEvent)
    {
        return JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), _jsonSerializerOptions);
    }

    public IDomainEvent Deserialize(string json, string assemblyQualifiedType)
    {
        var type = Type.GetType(assemblyQualifiedType, throwOnError: true)!;

        if (!type.IsAssignableTo(typeof(IDomainEvent)))
        {
            throw new InvalidOperationException($"{assemblyQualifiedType} is not assignable to {typeof(IDomainEvent).AssemblyQualifiedName}.");
        }

        return (JsonSerializer.Deserialize(json, type, _jsonSerializerOptions) as IDomainEvent)!;
    }
}
