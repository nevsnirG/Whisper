using System.Text.Json;

namespace Whisper.Outbox;
internal interface IDomainEventSerializer
{
    string Serialize(IDomainEvent domainEvent);
    IDomainEvent Deserialize(string json, string assemblyQualifiedType);
}

internal sealed class DomainEventSerializer(OutboxJsonOptions outboxJsonOptions) : IDomainEventSerializer
{
    private readonly JsonSerializerOptions _jsonSerializerOptions = BuildOptions(outboxJsonOptions);

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

    private static JsonSerializerOptions BuildOptions(OutboxJsonOptions options)
    {
        var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
        foreach (var converter in options.Converters)
        {
            jsonOptions.Converters.Add(converter);
        }
        return jsonOptions;
    }
}
