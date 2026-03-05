using System.Text.Json.Serialization;

namespace Whisper.Outbox;

/// <summary>
/// Options for configuring JSON serialization in the outbox.
/// </summary>
public sealed class OutboxJsonOptions
{
    /// <summary>
    /// Custom JSON converters to use when serializing and deserializing domain events.
    /// Register converters for value types (e.g., readonly record structs) that
    /// System.Text.Json cannot deserialize using the default parameterless constructor.
    /// </summary>
    public IList<JsonConverter> Converters { get; } = [];
}
