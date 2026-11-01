using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Whisper.Outbox;

/// <summary>
/// Options for configuring JSON serialization in the outbox.
/// </summary>
public sealed class OutboxJsonOptions
{
    /// <summary>
    /// The System.Text.Json options the outbox serializes and deserializes domain events with. The defaults round-trip
    /// the shapes domain events commonly have, without attributes on domain types: public fields
    /// (<see cref="JsonSerializerOptions.IncludeFields"/>) and properties with a private or protected setter.
    /// Change anything else here, e.g. a naming policy. Replacing <see cref="JsonSerializerOptions.TypeInfoResolver"/>
    /// (e.g. with a source-generated context) also replaces the support for non-public setters.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; } = new()
    {
        IncludeFields = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { AllowNonPublicSetters } },
    };

    /// <summary>
    /// Custom JSON converters to use when serializing and deserializing domain events: the converters of
    /// <see cref="SerializerOptions"/>. Register converters for value types (e.g., readonly record structs) that
    /// System.Text.Json cannot deserialize using the default parameterless constructor.
    /// </summary>
    public IList<JsonConverter> Converters => SerializerOptions.Converters;

    // System.Text.Json serializes a property through its public getter but silently skips it on deserialization when
    // the setter is private or protected, so the event would reach handlers with default values.
    private static void AllowNonPublicSetters(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        foreach (var property in typeInfo.Properties)
        {
            if (property.Set is not null || property.AttributeProvider is not PropertyInfo propertyInfo)
                continue;

            // Looked up on the declaring type: a private accessor declared in a base class is not visible
            // through a PropertyInfo reflected from a derived type.
            var setter = propertyInfo.DeclaringType?
                .GetProperty(propertyInfo.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)?
                .GetSetMethod(nonPublic: true);
            if (setter is not null)
                property.Set = (target, value) => setter.Invoke(target, [value]);
        }
    }
}
